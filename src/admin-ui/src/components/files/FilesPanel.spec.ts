import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import type { DocumentDetail, Source, UploadFile, UploadSummary } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { bffUser } from '@/test/fixtures'
import { FILES } from '@/utils/labels'
import FilesPanel from './FilesPanel.vue'

const source = { id: 's1', type: 'upload', name: 'Blanketter', settings: { maxFileSizeMb: 20 } } as unknown as Source
const slot = (id: string, fileName: string, state: string, reason: string | null = null): UploadFile => ({
  id, fileName, bytes: 2_500_000, state, reason, documentId: null, uploadedBy: 'anna', uploadedAt: '2026-11-01T10:00:00Z', replacedAt: null, deletedAt: null,
})
const summary = (overrides: Partial<UploadSummary> = {}): UploadSummary => ({
  total: 0, processing: 0, indexed: 0, failed: 0, bytes: 0, lastUploadAt: null, ...overrides,
})
const page = (items: UploadFile[], next: string | null = null) => ({ items, next })

function signIn(roles: string[] = ['rag-editor']): void {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ roles })
  auth.status = 'authenticated'
}

function pick(wrapper: ReturnType<typeof mount>, test: string, files: File[]): Promise<void> {
  const input = wrapper.get(`[data-test=${test}]`).element as HTMLInputElement
  Object.defineProperty(input, 'files', { value: files, configurable: true })
  return wrapper.get(`[data-test=${test}]`).trigger('change')
}

/** The confirm button of the open dialog with this text. */
function confirm(text: string): void {
  ;[...document.body.querySelectorAll('button')].filter((b) => b.textContent?.trim() === text).at(-1)!.click()
}

afterEach(() => {
  vi.useRealTimers()
})

describe('Files (admin design §4.5, §5.5.7, connectors §9)', () => {
  it('uploads chosen files to one batch, retries a failed one, and commits them as one sync', async () => {
    signIn()
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary({ total: 1, indexed: 1 }))
    vi.spyOn(api.sources, 'files').mockResolvedValue(page([slot('u1', 'taxa.pdf', 'indexed')]))
    vi.spyOn(api.sources, 'uploadBatch').mockResolvedValue({ batchId: 'b1', maxFiles: 100, maxBytes: 500 })
    const put = vi.spyOn(api.uploads, 'put')
      .mockResolvedValueOnce({ n: 1, uploadId: 'x', operation: 'add', fileName: 'a.pdf', bytes: 1, sha256: '' })
      .mockRejectedValueOnce(new ApiError(415, 'PUT', { code: 'upload_type_refused', params: { extension: '.exe' } }))
      .mockResolvedValueOnce({ n: 2, uploadId: 'y', operation: 'add', fileName: 'b.pdf', bytes: 1, sha256: '' })
    const commit = vi.spyOn(api.uploads, 'commit').mockResolvedValue({ batchId: 'b1', jobId: 'j', entries: 2 })
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true } })
    await flushPromises()

    expect(wrapper.text()).toContain('At most 20 MB per file')
    expect(wrapper.get('[data-test=file-list]').text()).toContain('taxa.pdf')
    await pick(wrapper, 'file-input', [new File(['a'], 'a.pdf'), new File(['b'], 'b.pdf')])
    await wrapper.get('[data-test=upload]').trigger('click')
    await flushPromises()
    expect(put).toHaveBeenCalledTimes(2)
    expect(put.mock.calls[0]!.slice(0, 2)).toEqual(['b1', 1])
    expect(put.mock.calls[0]![3]).toBe('a.pdf') // picked one by one: just the name
    expect(commit).not.toHaveBeenCalled()
    expect(wrapper.get('[data-test=pending]').text()).toContain('Failed')

    await wrapper.get('[data-test=upload]').trigger('click') // retries the failed file only, in the same batch
    await flushPromises()
    expect(put).toHaveBeenCalledTimes(3)
    expect(put.mock.calls[2]!.slice(0, 2)).toEqual(['b1', 2])
    expect(commit).toHaveBeenCalledWith('b1', { files: [1, 2], deletes: [] })
    expect(wrapper.emitted('committed')).toHaveLength(1)
    expect(wrapper.find('[data-test=pending]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('takes a dropped folder with its paths, lists refused files as skipped, and splits the rest over batches of the API limits', async () => {
    signIn()
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary())
    vi.spyOn(api.sources, 'files').mockResolvedValue(page([]))
    const batch = vi.spyOn(api.sources, 'uploadBatch')
      .mockResolvedValueOnce({ batchId: 'b1', maxFiles: 2, maxBytes: 500 })
      .mockResolvedValueOnce({ batchId: 'b2', maxFiles: 2, maxBytes: 500 })
    const put = vi.spyOn(api.uploads, 'put').mockResolvedValue({ n: 1, uploadId: 'x', operation: 'add', fileName: 'x', bytes: 1, sha256: '' })
    const commit = vi.spyOn(api.uploads, 'commit').mockResolvedValue({ batchId: 'b', jobId: 'j', entries: 1 })
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true } })
    await flushPromises()

    const entry = (f: File) => ({ isFile: true, isDirectory: false, name: f.name, file: (ok: (x: File) => void) => ok(f) })
    let read = false
    const folder = {
      isFile: false, isDirectory: true, name: 'Blanketter',
      createReader: () => ({ readEntries: (ok: (e: unknown[]) => void) => { ok(read ? [] : ['a.pdf', 'b.pdf', 'c.pdf', 'd.zip', '.DS_Store'].map((n) => entry(new File(['x'], n)))); read = true } }),
    }
    const loose = entry(new File(['x'], 'lös.txt'))
    await wrapper.get('[data-test=drop-zone]').trigger('drop', {
      dataTransfer: { items: [{ kind: 'file', webkitGetAsEntry: () => folder }, { kind: 'file', webkitGetAsEntry: () => loose }], files: [] },
    })
    await flushPromises()
    expect(wrapper.get('[data-test=pending-summary]').text()).toContain('4 files ready')
    expect(wrapper.get('[data-test=pending-summary]').text()).toContain('1 skipped')
    expect(wrapper.get('[data-test=pending]').text()).toContain('Blanketter/d.zip') // pending rows show the path
    expect(wrapper.get('[data-test=pending]').text()).not.toContain('.DS_Store')

    await wrapper.get('[data-test=upload]').trigger('click')
    await flushPromises()
    expect(batch).toHaveBeenCalledTimes(2)
    expect(put).toHaveBeenCalledTimes(4) // never the skipped zip
    expect(put.mock.calls.map((c) => [c[0], c[1], c[3]])).toEqual([
      ['b1', 1, 'Blanketter/a.pdf'], ['b1', 2, 'Blanketter/b.pdf'], ['b2', 1, 'Blanketter/c.pdf'], ['b2', 2, 'lös.txt'],
    ])
    expect(commit).toHaveBeenCalledWith('b1', { files: [1, 2], deletes: [] })
    expect(commit).toHaveBeenCalledWith('b2', { files: [1, 2], deletes: [] })
    expect(wrapper.emitted('committed')).toHaveLength(1)
    expect(wrapper.find('[data-test=pending]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('lists a large upload as counts and its first 50 rows, failed and skipped files first (#712)', async () => {
    signIn()
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary())
    vi.spyOn(api.sources, 'files').mockResolvedValue(page([]))
    vi.spyOn(api.sources, 'uploadBatch').mockResolvedValue({ batchId: 'b1', maxFiles: 100, maxBytes: 10_000 })
    vi.spyOn(api.uploads, 'put').mockImplementation(async (_batch, n) => {
      if (n === 65) throw new ApiError(500, 'PUT', { code: 'internal' })
      return { n, uploadId: 'x', operation: 'add', fileName: 'x', bytes: 1, sha256: '' }
    })
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true } })
    await flushPromises()

    const files = Array.from({ length: 69 }, (_, i) => new File(['x'], `f${String(i + 1).padStart(2, '0')}.pdf`))
    await pick(wrapper, 'file-input', [...files, new File(['x'], 'z.exe')])
    const rows = () => wrapper.findAll('[data-test=pending] li:not([data-test=pending-more])')
    expect(wrapper.get('[data-test=pending-summary]').text()).toContain('69 files ready')
    expect(rows()).toHaveLength(50)
    expect(rows()[0]!.text()).toContain('z.exe') // the skipped file before the waiting ones
    expect(wrapper.get('[data-test=pending-more]').text()).toBe(FILES.moreFiles(20))

    await wrapper.get('[data-test=upload]').trigger('click')
    await flushPromises()
    expect(rows()[0]!.text()).toContain('f65.pdf')
    expect(rows()[0]!.text()).toContain('Failed')
    expect(rows()[1]!.text()).toContain('z.exe')
    expect(wrapper.get('[data-test=sent-count]').text()).toBe(FILES.sentCount(68, 69))
    expect(wrapper.get('[data-test=pending-more]').text()).toBe(FILES.moreFiles(20))
    wrapper.unmount()
  })

  it('offers a folder picker that keeps each file\'s relative path', async () => {
    signIn()
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary())
    vi.spyOn(api.sources, 'files').mockResolvedValue(page([]))
    vi.spyOn(api.sources, 'uploadBatch').mockResolvedValue({ batchId: 'b1', maxFiles: 100, maxBytes: 500 })
    const put = vi.spyOn(api.uploads, 'put').mockResolvedValue({ n: 1, uploadId: 'x', operation: 'add', fileName: 'x', bytes: 1, sha256: '' })
    vi.spyOn(api.uploads, 'commit').mockResolvedValue({ batchId: 'b1', jobId: 'j', entries: 1 })
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true } })
    await flushPromises()
    expect(wrapper.get('[data-test=folder-input]').attributes()).toHaveProperty('webkitdirectory')

    const inFolder = (name: string, path: string) => Object.defineProperty(new File(['a'], name), 'webkitRelativePath', { value: path })
    await pick(wrapper, 'folder-input', [inFolder('semester.pdf', 'HR/Policys/semester.pdf'), inFolder('Thumbs.db', 'HR/Thumbs.db')])
    expect(wrapper.get('[data-test=pending-summary]').text()).toContain('1 file ready')
    expect(wrapper.get('[data-test=pending]').text()).toContain('HR/Policys/semester.pdf')
    await wrapper.get('[data-test=upload]').trigger('click')
    await flushPromises()
    expect(put.mock.calls[0]![3]).toBe('HR/Policys/semester.pdf')
    wrapper.unmount()
  })

  it('says that folders are kept, with a link to the Topics tab on the knowledge base page', async () => {
    signIn()
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary())
    vi.spyOn(api.sources, 'files').mockResolvedValue(page([]))
    const onSource = mount(FilesPanel, { props: { source, canEdit: true } })
    await flushPromises()
    expect(onSource.get('[data-test=folder-hint]').text()).toContain('HR/**')
    expect(onSource.find('[data-test=topics-link]').exists()).toBe(false)

    const onCollection = mount(FilesPanel, { props: { source, canEdit: true, topicsLink: true } })
    await flushPromises()
    expect(onCollection.get('[data-test=topics-link]').attributes('href')).toBe('?tab=topics')
    await onCollection.get('[data-test=topics-link]').trigger('click')
    expect(onCollection.emitted('topics')).toHaveLength(1)
  })

  it('replaces a slot with new bytes and deletes selected slots after a confirmation', async () => {
    signIn()
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary({ total: 2, indexed: 1, failed: 1 }))
    vi.spyOn(api.sources, 'files').mockResolvedValue(page([slot('u1', 'taxa.pdf', 'indexed'), slot('u2', 'gammal.doc', 'refused', 'upload.format_deferred')]))
    vi.spyOn(api.sources, 'uploadBatch').mockResolvedValueOnce({ batchId: 'b2', maxFiles: 100, maxBytes: 500 }).mockResolvedValueOnce({ batchId: 'b3', maxFiles: 100, maxBytes: 500 })
    const put = vi.spyOn(api.uploads, 'put').mockResolvedValue({ n: 1, uploadId: 'u1', operation: 'replace', fileName: 'taxa.pdf', bytes: 1, sha256: '' })
    const commit = vi.spyOn(api.uploads, 'commit').mockResolvedValue({ batchId: 'b', jobId: 'j', entries: 1 })
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true }, attachTo: document.body })
    await flushPromises()

    expect(wrapper.text()).toContain('Refused')
    expect(wrapper.text()).toContain('This file format is not supported yet.')
    await wrapper.get('[data-test=replace-u1]').trigger('click')
    const replacement = new File(['new'], 'taxa-2027.pdf')
    await pick(wrapper, 'replace-input', [replacement])
    await flushPromises()
    expect(put).toHaveBeenCalledWith('b2', 1, replacement, 'taxa-2027.pdf', 'u1')
    expect(commit).toHaveBeenCalledWith('b2', { files: [1], deletes: [] })

    await wrapper.get('[data-test=file-u2]').get('input[type=checkbox]').setValue(true)
    await wrapper.get('[data-test=delete-files]').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('Within 14 days the platform team can bring them back.')
    confirm('Remove 1 file')
    await flushPromises()
    expect(commit).toHaveBeenLastCalledWith('b3', { files: [], deletes: ['u2'] })
    wrapper.unmount()
  })

  it('lets a platform admin restore a removed file within 14 days (connectors §9.3)', async () => {
    signIn(['rag-platform-admin'])
    const days = (n: number) => new Date(Date.now() - n * 24 * 60 * 60 * 1000).toISOString()
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary({ total: 1, indexed: 1 }))
    const files = vi.spyOn(api.sources, 'files').mockImplementation(async (_id, query) => (query?.deleted
      ? page([{ ...slot('u3', 'taxa-2025.pdf', 'deleted'), deletedAt: days(3) }, { ...slot('u4', 'gammal.pdf', 'deleted'), deletedAt: days(15) }])
      : page([slot('u1', 'taxa.pdf', 'indexed')])))
    const restore = vi.spyOn(api.sources, 'restoreFiles').mockResolvedValue({ batchId: 'b', jobId: 'j', entries: 1 })
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true }, attachTo: document.body })
    await flushPromises()
    expect(wrapper.find('[data-test^=restore-]').exists()).toBe(false) // only among the removed files

    await wrapper.get('[data-test=show-deleted]').trigger('click')
    await flushPromises()
    expect(files).toHaveBeenLastCalledWith('s1', expect.objectContaining({ deleted: true }))
    expect(wrapper.find('[data-test=restore-u4]').exists()).toBe(false)
    expect(wrapper.get('[data-test=restore-expired-u4]').text()).toContain('can no longer be restored')

    await wrapper.get('[data-test=restore-u3]').trigger('click')
    await flushPromises()
    confirm('Restore')
    await flushPromises()
    expect(restore).toHaveBeenCalledWith('s1', ['u3'])
    expect(wrapper.emitted('committed')).toHaveLength(1)
    wrapper.unmount()
  })

  it('offers no restore to a department admin', async () => {
    signIn(['rag-department-admin'])
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary())
    vi.spyOn(api.sources, 'files').mockResolvedValue(page([{ ...slot('u3', 'taxa-2025.pdf', 'deleted'), deletedAt: new Date().toISOString() }]))
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true } })
    await flushPromises()
    await wrapper.get('[data-test=show-deleted]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-test^=restore-]').exists()).toBe(false)
  })

  it('shows a reader the list without the drop zone, selection or Retry failed', async () => {
    signIn(['rag-viewer'])
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary({ total: 1, failed: 1 }))
    vi.spyOn(api.sources, 'files').mockResolvedValue(page([slot('u1', 'taxa.pdf', 'refused', 'upload.archive')]))
    const wrapper = mount(FilesPanel, { props: { source, canEdit: false } })
    await flushPromises()
    expect(wrapper.find('[data-test=drop-zone]').exists()).toBe(false)
    expect(wrapper.find('input[type=checkbox]').exists()).toBe(false)
    expect(wrapper.find('[data-test=retry-failed]').exists()).toBe(false)
    expect(wrapper.find('[data-test=replace-u1]').exists()).toBe(false)
    expect(wrapper.get('[data-test=file-list]').text()).toContain('taxa.pdf')
  })

  it('notes a file that was not scanned for malware, in its row and its details, and not a scanned one (#535)', async () => {
    signIn(['rag-viewer'])
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary({ total: 3, processing: 1, indexed: 2 }))
    vi.spyOn(api.sources, 'files').mockResolvedValue(page([
      slot('u1', 'pending.pdf', 'unscanned'),
      { ...slot('u2', 'old.pdf', 'indexed'), scannedAt: null },
      { ...slot('u3', 'scanned.pdf', 'indexed'), scannedAt: '2026-11-01T10:01:00Z' },
    ]))
    const wrapper = mount(FilesPanel, { props: { source, canEdit: false }, attachTo: document.body })
    await flushPromises()
    expect(wrapper.get('[data-test=not-scanned-u1]').text()).toBe(FILES.notScanned)
    expect(wrapper.get('[data-test=not-scanned-u2]').text()).toBe(FILES.notScanned)
    expect(wrapper.find('[data-test=not-scanned-u3]').exists()).toBe(false)
    expect(wrapper.get('[data-test=file-list]').text()).toContain(FILES.states.unscanned)
    await wrapper.get('[data-test=open-u2]').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('[data-test=detail-not-scanned]')?.textContent).toBe(FILES.notScanned)
    wrapper.unmount()
  })

  it('shows the summary bar and refreshes it and the rows every 3 s while files are processing', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    signIn()
    const counts = vi.spyOn(api.sources, 'filesSummary')
      .mockResolvedValueOnce(summary({ total: 120, processing: 34, indexed: 84, failed: 2 }))
      .mockResolvedValueOnce(summary({ total: 120, processing: 10, indexed: 108, failed: 2 }))
      .mockResolvedValue(summary({ total: 120, processing: 0, indexed: 118, failed: 2 }))
    const files = vi.spyOn(api.sources, 'files').mockResolvedValue(page([slot('u1', 'taxa.pdf', 'received')]))
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true } })
    await flushPromises()

    expect(wrapper.get('[data-test=files-summary-text]').text()).toBe('120 files · 34 processing · 84 indexed · 2 failed')
    expect(wrapper.get('[data-test=files-progress]').attributes('aria-valuenow')).toBe('72') // (84 + 2) / 120
    expect(counts).toHaveBeenCalledTimes(1)

    await vi.advanceTimersByTimeAsync(2999)
    expect(counts).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1)
    await flushPromises()
    expect(counts).toHaveBeenCalledTimes(2)
    expect(files).toHaveBeenCalledTimes(2) // the visible rows too
    expect(wrapper.get('[data-test=files-summary-text]').text()).toContain('10 processing')

    await vi.advanceTimersByTimeAsync(3000)
    await flushPromises()
    expect(counts).toHaveBeenCalledTimes(3)
    expect(wrapper.get('[data-test=files-progress]').attributes('aria-valuenow')).toBe('100')

    // Nothing processing: polling stops.
    await vi.advanceTimersByTimeAsync(30_000)
    expect(counts).toHaveBeenCalledTimes(3)
    wrapper.unmount()
  })

  it('keeps every loaded row, the cursor and the selection when a poll refreshes more than 200 rows (#709)', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    signIn()
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary({ total: 500, processing: 500 }))
    let all = Array.from({ length: 500 }, (_, i) => slot(`u${i + 1}`, `f${String(i + 1).padStart(3, '0')}.pdf`, 'received'))
    const files = vi.spyOn(api.sources, 'files').mockImplementation(async (_id, query) => {
      const start = query?.after ? Number(query.after) : 0
      const end = start + (query?.limit ?? 50)
      return page(all.slice(start, end), end < all.length ? String(end) : null)
    })
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true } })
    await flushPromises()
    for (let i = 0; i < 5; i++) {
      await wrapper.get('[data-test=files-more]').trigger('click')
      await flushPromises()
    }
    expect(wrapper.findAll('[data-test^=file-u]')).toHaveLength(300)
    await wrapper.get('[data-test=file-u10]').get('input[type=checkbox]').setValue(true)
    await wrapper.get('[data-test=file-u250]').get('input[type=checkbox]').setValue(true)
    expect(wrapper.get('[data-test=delete-files]').text()).toBe('Remove 2 files')

    all = all.map((f) => (f.id === 'u250' ? { ...f, state: 'indexed' } : f))
    files.mockClear()
    await vi.advanceTimersByTimeAsync(3000)
    await flushPromises()

    // The loaded pages are read again from the top, at most 200 rows per request.
    expect(files.mock.calls.map(([, query]) => [query?.after, query?.limit])).toEqual([[undefined, 200], ['200', 100]])
    expect(wrapper.findAll('[data-test^=file-u]')).toHaveLength(300)
    expect(wrapper.get('[data-test=file-u250]').text()).toContain(FILES.states.indexed)
    expect(wrapper.get('[data-test=delete-files]').text()).toBe('Remove 2 files')
    expect((wrapper.get('[data-test=file-u250]').get('input[type=checkbox]').element as HTMLInputElement).checked).toBe(true)

    files.mockClear()
    await wrapper.get('[data-test=files-more]').trigger('click') // the cursor still points after row 300
    await flushPromises()
    expect(files).toHaveBeenLastCalledWith('s1', expect.objectContaining({ after: '300', limit: 50 }))
    expect(wrapper.findAll('[data-test^=file-u]')).toHaveLength(350)
    wrapper.unmount()
  })

  it('stops polling while the page is hidden and when the card goes away, and starts again after an upload', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    signIn()
    let processing = 5
    const counts = vi.spyOn(api.sources, 'filesSummary').mockImplementation(async () => summary({ total: 5, processing, indexed: 5 - processing }))
    vi.spyOn(api.sources, 'files').mockResolvedValue(page([]))
    vi.spyOn(api.sources, 'uploadBatch').mockResolvedValue({ batchId: 'b1', maxFiles: 100, maxBytes: 500 })
    vi.spyOn(api.uploads, 'put').mockResolvedValue({ n: 1, uploadId: 'x', operation: 'add', fileName: 'a.pdf', bytes: 1, sha256: '' })
    vi.spyOn(api.uploads, 'commit').mockResolvedValue({ batchId: 'b1', jobId: 'j', entries: 1 })
    const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(false)
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true } })
    await flushPromises()
    expect(counts).toHaveBeenCalledTimes(1)

    hidden.mockReturnValue(true)
    document.dispatchEvent(new Event('visibilitychange'))
    await vi.advanceTimersByTimeAsync(10_000)
    expect(counts).toHaveBeenCalledTimes(1)

    hidden.mockReturnValue(false)
    document.dispatchEvent(new Event('visibilitychange')) // visible again: refreshes at once and goes on
    await flushPromises()
    expect(counts).toHaveBeenCalledTimes(2)
    processing = 0
    await vi.advanceTimersByTimeAsync(3000)
    await flushPromises()
    expect(counts).toHaveBeenCalledTimes(3)
    await vi.advanceTimersByTimeAsync(10_000)
    expect(counts).toHaveBeenCalledTimes(3) // nothing processing

    processing = 1
    await pick(wrapper, 'file-input', [new File(['a'], 'a.pdf')])
    await wrapper.get('[data-test=upload]').trigger('click')
    await flushPromises()
    expect(counts).toHaveBeenCalledTimes(4) // right after the commit
    await vi.advanceTimersByTimeAsync(3000)
    await flushPromises()
    expect(counts).toHaveBeenCalledTimes(5)

    wrapper.unmount()
    await vi.advanceTimersByTimeAsync(10_000)
    expect(counts).toHaveBeenCalledTimes(5)
  })

  it('searches (debounced), filters by state with the summary\'s counts and sorts, from the first page each time', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    signIn()
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary({ total: 120, processing: 0, indexed: 118, failed: 2 }))
    const files = vi.spyOn(api.sources, 'files').mockImplementation(async (_id, query) =>
      query?.after ? page([slot('u2', 'b.pdf', 'indexed')]) : page([slot('u1', 'a.pdf', 'indexed')], 'cursor-1'))
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true } })
    await flushPromises()
    expect(files).toHaveBeenLastCalledWith('s1', { deleted: undefined, q: undefined, state: undefined, sort: 'name', after: undefined, limit: 50 })

    const chips = wrapper.get('[data-test=files-filters]').findAll('button').map((b) => b.text().replace('check_circle', '').trim())
    expect(chips).toEqual(['All (120)', 'Processing (0)', 'Indexed (118)', 'Failed (2)'])

    await wrapper.get('[data-test=files-more]').trigger('click')
    await flushPromises()
    expect(files).toHaveBeenLastCalledWith('s1', expect.objectContaining({ after: 'cursor-1' }))
    expect(wrapper.get('[data-test=file-list]').text()).toContain('b.pdf')

    await wrapper.get('#files-search').setValue('  HR/ ')
    await vi.advanceTimersByTimeAsync(299)
    expect(files).toHaveBeenCalledTimes(2)
    await vi.advanceTimersByTimeAsync(1)
    await flushPromises()
    expect(files).toHaveBeenLastCalledWith('s1', expect.objectContaining({ q: 'HR/', after: undefined }))
    expect(wrapper.get('[data-test=file-list]').text()).not.toContain('b.pdf') // paging starts over

    await wrapper.get('[data-test=filter-failed]').trigger('click')
    await flushPromises()
    expect(files).toHaveBeenLastCalledWith('s1', expect.objectContaining({ q: 'HR/', state: 'failed', after: undefined }))
    expect(wrapper.get('[data-test=filter-failed]').attributes('aria-pressed')).toBe('true')

    await wrapper.get('[data-test=files-sort] select').setValue('uploaded')
    await flushPromises()
    expect(files).toHaveBeenLastCalledWith('s1', expect.objectContaining({ q: 'HR/', state: 'failed', sort: 'uploaded', after: undefined }))

    await wrapper.get('[data-test=filter-all]').trigger('click')
    await flushPromises()
    expect(files).toHaveBeenLastCalledWith('s1', expect.objectContaining({ state: undefined }))
    wrapper.unmount()
  })

  it('groups the rows under folder headers when sorted by name, files without a folder first', async () => {
    signIn()
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary({ total: 5, indexed: 5 }))
    vi.spyOn(api.sources, 'files').mockResolvedValue(page([
      slot('u1', 'HR/policy.pdf', 'indexed'), slot('u2', 'HR/Policys/semester.pdf', 'indexed'), slot('u3', 'HR/zz.pdf', 'indexed'),
      slot('u4', 'lös.pdf', 'indexed'), slot('u5', 'Ekonomi/budget.xlsx', 'indexed'),
    ]))
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true } })
    await flushPromises()

    expect(wrapper.findAll('[data-test=folder-header]').map((h) => h.text().replace('folder', ''))).toEqual(['HR', 'HR/Policys', 'Ekonomi'])
    const sections = wrapper.get('[data-test=file-list]').findAll('section').map((s) => s.attributes('data-test'))
    expect(sections).toEqual(['folder-root', 'folder-HR', 'folder-HR/Policys', 'folder-Ekonomi'])
    expect(wrapper.get('[data-test=folder-root]').text()).toContain('lös.pdf')
    expect(wrapper.get('[data-test=folder-HR]').findAll('[data-test^=open-]').map((b) => b.text())).toEqual(['policy.pdf', 'zz.pdf'])

    // Newest first: one list with each whole path.
    await wrapper.get('[data-test=files-sort] select').setValue('uploaded')
    await flushPromises()
    expect(wrapper.find('[data-test=folder-header]').exists()).toBe(false)
    expect(wrapper.get('[data-test=open-u2]').text()).toBe('HR/Policys/semester.pdf')
  })

  it('selects all loaded rows and removes them after a confirmation', async () => {
    signIn()
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary({ total: 2, indexed: 2 }))
    vi.spyOn(api.sources, 'files').mockResolvedValue(page([slot('u1', 'a.pdf', 'indexed'), slot('u2', 'HR/b.pdf', 'indexed')]))
    vi.spyOn(api.sources, 'uploadBatch').mockResolvedValue({ batchId: 'b9', maxFiles: 100, maxBytes: 500 })
    const commit = vi.spyOn(api.uploads, 'commit').mockResolvedValue({ batchId: 'b9', jobId: 'j', entries: 2 })
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true }, attachTo: document.body })
    await flushPromises()

    await wrapper.get('[data-test=select-all] input').setValue(true)
    expect(wrapper.get('[data-test=delete-files]').text()).toBe('Remove 2 files')
    await wrapper.get('[data-test=delete-files]').trigger('click')
    await flushPromises()
    confirm('Remove 2 files')
    await flushPromises()
    expect(commit).toHaveBeenCalledWith('b9', { files: [], deletes: ['u1', 'u2'] })
    wrapper.unmount()
  })

  it('retries the failed files and polls for their progress', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    signIn()
    const counts = vi.spyOn(api.sources, 'filesSummary')
      .mockResolvedValueOnce(summary({ total: 3, indexed: 1, failed: 2 }))
      .mockResolvedValue(summary({ total: 3, processing: 2, indexed: 1 }))
    vi.spyOn(api.sources, 'files').mockResolvedValue(page([slot('u1', 'a.pdf', 'refused', 'upload.type_unknown')]))
    const retry = vi.spyOn(api.sources, 'retryFailed').mockResolvedValue({ jobId: 'j', runId: null, alreadyRunning: false } as never)
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true } })
    await flushPromises()

    await wrapper.get('[data-test=retry-failed]').trigger('click')
    await flushPromises()
    expect(retry).toHaveBeenCalledWith('s1')
    expect(counts).toHaveBeenCalledTimes(2)
    expect(wrapper.find('[data-test=retry-failed]').exists()).toBe(false) // nothing failed now
    await vi.advanceTimersByTimeAsync(3000)
    await flushPromises()
    expect(counts).toHaveBeenCalledTimes(3)
    wrapper.unmount()
  })

  it('opens a file\'s details from its row: folder, size, uploaded, state and reason, passages, users; Replace keeps the folder', async () => {
    signIn()
    const indexed: UploadFile = { ...slot('u1', 'HR/Policys/semester.pdf', 'indexed'), documentId: 'd1', replacedAt: '2026-11-03T08:00:00Z' }
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary({ total: 2, indexed: 1, failed: 1 }))
    vi.spyOn(api.sources, 'files').mockResolvedValue(page([indexed, slot('u2', 'gammal.doc', 'malware', 'upload.archive')]))
    const document_: DocumentDetail = {
      id: 'd1', sourceId: 's1', title: 'Semester', url: null, displayName: 'HR/Policys/semester.pdf', mediaType: 'application/pdf', liveChunkSetId: 'cs2',
      chunkSets: [{ id: 'cs1', state: 'superseded', chunkCount: 3 }, { id: 'cs2', state: 'live', chunkCount: 12 }],
    }
    const get = vi.spyOn(api.documents, 'get').mockResolvedValue(document_)
    const chunk = (n: number) => ({ id: `c${n}`, ordinal: n, headingPath: n === 1 ? 'Semester > Ansökan' : null, text: `Text ${n}`, tokenCount: 10, page: 1, pageEnd: 1, contentKind: 'text' })
    const chunks = vi.spyOn(api.documents, 'chunks').mockResolvedValue({ documentId: 'd1', chunkSetId: 'cs2', chunks: [1, 2, 3, 4, 5].map(chunk), truncated: false })
    vi.spyOn(api.sources, 'uploadBatch').mockResolvedValue({ batchId: 'b1', maxFiles: 100, maxBytes: 500 })
    const put = vi.spyOn(api.uploads, 'put').mockResolvedValue({ n: 1, uploadId: 'u1', operation: 'replace', fileName: 'x', bytes: 1, sha256: '' })
    vi.spyOn(api.uploads, 'commit').mockResolvedValue({ batchId: 'b1', jobId: 'j', entries: 1 })
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true, usedBy: 'Bygglovsassistenten' }, attachTo: document.body })
    await flushPromises()
    expect(get).not.toHaveBeenCalled() // only when the panel opens

    const row = wrapper.get('[data-test=open-u1]')
    expect(row.element.tagName).toBe('BUTTON') // keyboard: a button
    expect(row.attributes('aria-label')).toBe('Details of HR/Policys/semester.pdf')
    await row.trigger('click')
    await flushPromises()
    const details = wrapper.get('[data-test=file-details]')
    expect(details.get('[data-test=detail-folder]').text()).toBe('HR/Policys')
    expect(details.text()).toContain('2,4 MB')
    expect(details.text()).toContain('PDF')
    expect(details.get('[data-test=detail-uploaded]').text()).not.toContain('anna')
    expect(details.text()).toContain('Replaced')
    expect(details.text()).toContain('Searchable')
    expect(details.get('[data-test=detail-used-by]').text()).toBe('Bygglovsassistenten')
    expect(get).toHaveBeenCalledWith('d1')
    expect(chunks).toHaveBeenCalledWith('d1')
    expect(wrapper.get('[data-test=chunk-count]').text()).toContain('12 passages')
    expect(wrapper.findAll('[data-test=chunk]').map((c) => c.text())).toEqual(['Semester > AnsökanText 1', 'Text 2', 'Text 3'])

    await wrapper.get('[data-test=detail-replace]').trigger('click')
    const replacement = new File(['new'], 'semester-2027.pdf')
    await pick(wrapper, 'replace-input', [replacement])
    await flushPromises()
    expect(put).toHaveBeenCalledWith('b1', 1, replacement, 'HR/Policys/semester-2027.pdf', 'u1')

    await wrapper.get('[data-test=open-u2]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-test=detail-reason]').text()).toBe('Archives (such as zip files) are not accepted. Upload the files inside instead.')
    expect(wrapper.find('[data-test=detail-chunks]').exists()).toBe(false)
    expect(get).toHaveBeenCalledTimes(1)
    await wrapper.get('[data-test=detail-delete]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-test=file-details]').exists()).toBe(false)
    expect(document.body.textContent).toContain('Remove 1 file?')
    wrapper.unmount()
  })

  it('shows a reader the passage count of an indexed file without asking for the passages', async () => {
    signIn(['rag-viewer'])
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary({ total: 1, indexed: 1 }))
    vi.spyOn(api.sources, 'files').mockResolvedValue(page([{ ...slot('u1', 'a.pdf', 'indexed'), documentId: 'd1' }]))
    vi.spyOn(api.documents, 'get').mockResolvedValue({
      id: 'd1', sourceId: 's1', title: null, url: null, displayName: 'a.pdf', mediaType: null, liveChunkSetId: 'cs', chunkSets: [{ id: 'cs', state: 'live', chunkCount: 1 }],
    })
    const chunks = vi.spyOn(api.documents, 'chunks')
    const wrapper = mount(FilesPanel, { props: { source, canEdit: false } })
    await flushPromises()
    await wrapper.get('[data-test=open-u1]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-test=chunk-count]').text()).toBe('1 passage')
    expect(chunks).not.toHaveBeenCalled()
    expect(wrapper.find('[data-test=detail-replace]').exists()).toBe(false)
    expect(wrapper.find('[data-test=detail-used-by]').exists()).toBe(false) // the source page has no users to show
  })
})

describe('Files: a known name replaces by default (admin design §5.5.7 point 6, support case SA-11)', () => {
  const put = () => vi.spyOn(api.uploads, 'put').mockImplementation(async (_batch, n, _file, path, replaces) => ({
    n, uploadId: replaces ?? `new-${n}`, operation: replaces ? 'replace' : 'add', fileName: path, bytes: 1, sha256: '',
  }))

  /** The panel over these live files, with batches and commits that succeed. */
  async function panel(live: UploadFile[]) {
    signIn()
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary({ total: live.length, indexed: live.length }))
    const files = vi.spyOn(api.sources, 'files').mockResolvedValue(page(live))
    vi.spyOn(api.sources, 'uploadBatch').mockResolvedValue({ batchId: 'b1', maxFiles: 100, maxBytes: 500 })
    const commit = vi.spyOn(api.uploads, 'commit').mockResolvedValue({ batchId: 'b1', jobId: 'j', entries: 1 })
    const notify = vi.spyOn(useUiStore(), 'notify')
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true }, attachTo: document.body })
    await flushPromises()
    files.mockClear()
    return { wrapper, files, commit, notify }
  }

  async function upload(wrapper: ReturnType<typeof mount>): Promise<void> {
    await wrapper.get('[data-test=upload]').trigger('click')
    await flushPromises()
  }

  const inFolder = (name: string, path: string) => Object.defineProperty(new File(['a'], name), 'webkitRelativePath', { value: path })

  it('lists the matching files once and proposes Replace, which sends each into its slot; the rest upload as new', async () => {
    const { wrapper, files, commit, notify } = await panel([slot('u1', 'taxa.pdf', 'indexed'), slot('u2', 'blankett.pdf', 'indexed')])
    const sent = put()
    const taxa = new File(['ny'], 'taxa.pdf')
    const fresh = new File(['x'], 'ny.pdf')
    await pick(wrapper, 'file-input', [taxa, fresh])
    await upload(wrapper)

    expect(files).toHaveBeenCalledWith('s1', { q: undefined, sort: 'name', after: undefined, limit: 200 }) // no shared start: every file
    expect(sent).not.toHaveBeenCalled() // nothing before the answer
    const dialog = wrapper.get('[data-test=known-files]')
    expect(dialog.text()).toContain(FILES.knownTitle(1))
    expect(dialog.get('[data-test=known-list]').findAll('li').map((li) => li.text())).toEqual(['taxa.pdf'])
    expect(document.activeElement).toBe(dialog.get('[data-test=known-replace]').element) // the default: Enter replaces
    expect(dialog.get('[data-test=known-replace]').text()).toBe(FILES.replaceKnown(1))

    await dialog.get('[data-test=known-replace]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-test=known-files]').exists()).toBe(false)
    expect(sent).toHaveBeenCalledWith('b1', 1, taxa, 'taxa.pdf', 'u1')
    expect(sent).toHaveBeenCalledWith('b1', 2, fresh, 'ny.pdf', undefined)
    expect(commit).toHaveBeenCalledWith('b1', { files: [1, 2], deletes: [] })
    expect(notify).toHaveBeenCalledWith(FILES.committedReplacing(2, 1))
    wrapper.unmount()
  })

  it('uploads the matching files as new ones with Keep both', async () => {
    const { wrapper, commit } = await panel([slot('u1', 'taxa.pdf', 'indexed')])
    const sent = put()
    const taxa = new File(['ny'], 'taxa.pdf')
    await pick(wrapper, 'file-input', [taxa])
    await upload(wrapper)

    await wrapper.get('[data-test=known-keep]').trigger('click')
    await flushPromises()
    expect(sent).toHaveBeenCalledTimes(1)
    expect(sent).toHaveBeenCalledWith('b1', 1, taxa, 'taxa.pdf', undefined)
    expect(commit).toHaveBeenCalledWith('b1', { files: [1], deletes: [] })
    wrapper.unmount()
  })

  it('shows no dialog when no name matches, and looks a single file up by its whole path', async () => {
    const { wrapper, files, commit } = await panel([slot('u1', 'taxa.pdf', 'indexed')])
    const sent = put()
    await pick(wrapper, 'file-input', [new File(['x'], 'taxa-2027.pdf')])
    await upload(wrapper)

    expect(files).toHaveBeenCalledWith('s1', expect.objectContaining({ q: 'taxa-2027.pdf' }))
    expect(wrapper.find('[data-test=known-files]').exists()).toBe(false)
    expect(sent).toHaveBeenCalledWith('b1', 1, expect.any(File), 'taxa-2027.pdf', undefined)
    expect(commit).toHaveBeenCalledWith('b1', { files: [1], deletes: [] })
    wrapper.unmount()
  })

  it('matches the folder as part of the name: the same file name in another folder is a new file', async () => {
    const { wrapper, files } = await panel([slot('u1', 'HR/semester.pdf', 'indexed'), slot('u2', 'HR/Policys/semester.pdf', 'indexed')])
    const sent = put()
    await pick(wrapper, 'folder-input', [inFolder('semester.pdf', 'HR/Policys/semester.pdf'), inFolder('semester.pdf', 'HR/Ekonomi/semester.pdf')])
    await upload(wrapper)

    expect(files).toHaveBeenCalledWith('s1', expect.objectContaining({ q: 'HR/' })) // the folder both share
    expect(wrapper.get('[data-test=known-list]').findAll('li').map((li) => li.text())).toEqual(['HR/Policys/semester.pdf'])
    await wrapper.get('[data-test=known-replace]').trigger('click')
    await flushPromises()
    expect(sent.mock.calls.map((c) => [c[3], c[4]])).toEqual([['HR/Policys/semester.pdf', 'u2'], ['HR/Ekonomi/semester.pdf', undefined]])
    wrapper.unmount()
  })

  it('ignores case, takes the newest of two live files with the path, and reads every page of the lookup', async () => {
    signIn()
    const older = slot('u1', 'Rutiner/Lösenord.docx', 'indexed')
    const newer = { ...slot('u2', 'rutiner/lösenord.docx', 'indexed'), uploadedAt: '2026-11-05T10:00:00Z' }
    vi.spyOn(api.sources, 'filesSummary').mockResolvedValue(summary({ total: 2, indexed: 2 }))
    const files = vi.spyOn(api.sources, 'files').mockImplementation(async (_id, query) =>
      query?.limit === 200 ? (query.after ? page([older]) : page([newer], 'c1')) : page([older, newer]))
    vi.spyOn(api.sources, 'uploadBatch').mockResolvedValue({ batchId: 'b1', maxFiles: 100, maxBytes: 500 })
    vi.spyOn(api.uploads, 'commit').mockResolvedValue({ batchId: 'b1', jobId: 'j', entries: 1 })
    const sent = put()
    const wrapper = mount(FilesPanel, { props: { source, canEdit: true }, attachTo: document.body })
    await flushPromises()
    await pick(wrapper, 'folder-input', [inFolder('Lösenord.docx', 'Rutiner/Lösenord.docx')])
    await upload(wrapper)

    expect(files).toHaveBeenCalledWith('s1', expect.objectContaining({ limit: 200, after: 'c1' }))
    await wrapper.get('[data-test=known-replace]').trigger('click')
    await flushPromises()
    expect(sent).toHaveBeenCalledWith('b1', 1, expect.any(File), 'Rutiner/Lösenord.docx', 'u2')
    wrapper.unmount()
  })

  it('sends nothing when the dialog is closed without a choice, asks again on the next upload, and not again on a retry', async () => {
    const { wrapper, files } = await panel([slot('u1', 'taxa.pdf', 'indexed')])
    const sent = vi.spyOn(api.uploads, 'put')
      .mockRejectedValueOnce(new ApiError(500, 'PUT', { code: 'internal' }))
      .mockResolvedValue({ n: 1, uploadId: 'u1', operation: 'replace', fileName: 'taxa.pdf', bytes: 1, sha256: '' })
    await pick(wrapper, 'file-input', [new File(['ny'], 'taxa.pdf')])
    await upload(wrapper)
    await wrapper.get('[data-test=known-cancel]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-test=known-files]').exists()).toBe(false)
    expect(sent).not.toHaveBeenCalled()
    expect(wrapper.get('[data-test=pending-summary]').text()).toContain('1 file ready')

    await upload(wrapper)
    await wrapper.get('[data-test=known-replace]').trigger('click')
    await flushPromises()
    expect(sent).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[data-test=pending]').text()).toContain('Failed')

    files.mockClear()
    await upload(wrapper) // the retry keeps the answer
    expect(files).not.toHaveBeenCalledWith('s1', expect.objectContaining({ limit: 200 }))
    expect(wrapper.find('[data-test=known-files]').exists()).toBe(false)
    expect(sent).toHaveBeenLastCalledWith('b1', 1, expect.any(File), 'taxa.pdf', 'u1')
    wrapper.unmount()
  })
})

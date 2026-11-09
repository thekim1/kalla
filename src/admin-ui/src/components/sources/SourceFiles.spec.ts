import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import type { Source, UploadFile } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import SourceFiles from './SourceFiles.vue'
import SourceQuarantine from './SourceQuarantine.vue'

const source = { id: 's1', type: 'upload', name: 'Blanketter', settings: { maxFileSizeMb: 20 } } as unknown as Source
const slot = (id: string, fileName: string, state: string, reason: string | null = null): UploadFile => ({
  id, fileName, bytes: 2_500_000, state, reason, documentId: null, uploadedBy: 'anna', uploadedAt: '2026-11-01T10:00:00Z', replacedAt: null, deletedAt: null,
})

function pick(wrapper: ReturnType<typeof mount>, test: string, files: File[]): Promise<void> {
  const input = wrapper.get(`[data-test=${test}]`).element as HTMLInputElement
  Object.defineProperty(input, 'files', { value: files, configurable: true })
  return wrapper.get(`[data-test=${test}]`).trigger('change')
}

describe('Files (admin design §4.5, connectors §9)', () => {
  it('uploads chosen files to one batch, retries a failed one, and commits them as one sync', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.sources, 'files').mockResolvedValue({ items: [slot('u1', 'taxa.pdf', 'indexed')], next: null })
    vi.spyOn(api.sources, 'uploadBatch').mockResolvedValue({ batchId: 'b1', maxFiles: 100, maxBytes: 500 })
    const put = vi.spyOn(api.uploads, 'put')
      .mockResolvedValueOnce({ n: 1, uploadId: 'x', operation: 'add', fileName: 'a.pdf', bytes: 1, sha256: '' })
      .mockRejectedValueOnce(new ApiError(415, 'PUT', { code: 'upload_type_refused', params: { extension: '.exe' } }))
      .mockResolvedValueOnce({ n: 2, uploadId: 'y', operation: 'add', fileName: 'b.pdf', bytes: 1, sha256: '' })
    const commit = vi.spyOn(api.uploads, 'commit').mockResolvedValue({ batchId: 'b1', jobId: 'j', entries: 2 })
    const wrapper = mount(SourceFiles, { props: { source, canEdit: true } })
    await flushPromises()

    expect(wrapper.text()).toContain('At most 20 MB per file')
    expect(wrapper.get('[data-test=file-list]').text()).toContain('taxa.pdf')
    await pick(wrapper, 'file-input', [new File(['a'], 'a.pdf'), new File(['b'], 'b.pdf')])
    await wrapper.get('[data-test=upload]').trigger('click')
    await flushPromises()
    expect(put).toHaveBeenCalledTimes(2)
    expect(commit).not.toHaveBeenCalled()
    expect(wrapper.get('[data-test=pending]').text()).toContain('Failed')

    await wrapper.get('[data-test=upload]').trigger('click') // retries the failed file only, in the same batch
    await flushPromises()
    expect(put).toHaveBeenCalledTimes(3)
    expect(put.mock.calls[2]!.slice(0, 2)).toEqual(['b1', 2])
    expect(commit).toHaveBeenCalledWith('b1', { files: [1, 2], deletes: [] })
    expect(wrapper.emitted('committed')).toHaveLength(1)
    expect(wrapper.find('[data-test=pending]').exists()).toBe(false)
  })

  it('takes a dropped folder, lists refused files as skipped, and splits the rest over batches of the API limits', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.sources, 'files').mockResolvedValue({ items: [], next: null })
    const batch = vi.spyOn(api.sources, 'uploadBatch')
      .mockResolvedValueOnce({ batchId: 'b1', maxFiles: 2, maxBytes: 500 })
      .mockResolvedValueOnce({ batchId: 'b2', maxFiles: 2, maxBytes: 500 })
    const put = vi.spyOn(api.uploads, 'put').mockResolvedValue({ n: 1, uploadId: 'x', operation: 'add', fileName: 'x', bytes: 1, sha256: '' })
    const commit = vi.spyOn(api.uploads, 'commit').mockResolvedValue({ batchId: 'b', jobId: 'j', entries: 1 })
    const wrapper = mount(SourceFiles, { props: { source, canEdit: true } })
    await flushPromises()

    const entry = (f: File) => ({ isFile: true, isDirectory: false, name: f.name, file: (ok: (x: File) => void) => ok(f) })
    let read = false
    const folder = {
      isFile: false, isDirectory: true, name: 'Blanketter',
      createReader: () => ({ readEntries: (ok: (e: unknown[]) => void) => { ok(read ? [] : ['a.pdf', 'b.pdf', 'c.pdf', 'd.zip', '.DS_Store'].map((n) => entry(new File(['x'], n)))); read = true } }),
    }
    await wrapper.get('[data-test=drop-zone]').trigger('drop', { dataTransfer: { items: [{ kind: 'file', webkitGetAsEntry: () => folder }], files: [] } })
    await flushPromises()
    expect(wrapper.get('[data-test=pending-summary]').text()).toContain('3 files ready')
    expect(wrapper.get('[data-test=pending-summary]').text()).toContain('1 skipped')
    expect(wrapper.get('[data-test=pending]').text()).toContain('d.zip')
    expect(wrapper.get('[data-test=pending]').text()).not.toContain('.DS_Store')

    await wrapper.get('[data-test=upload]').trigger('click')
    await flushPromises()
    expect(batch).toHaveBeenCalledTimes(2)
    expect(put).toHaveBeenCalledTimes(3) // never the skipped zip
    expect(put.mock.calls.map((c) => c.slice(0, 2))).toEqual([['b1', 1], ['b1', 2], ['b2', 1]])
    expect(commit).toHaveBeenCalledWith('b1', { files: [1, 2], deletes: [] })
    expect(commit).toHaveBeenCalledWith('b2', { files: [1], deletes: [] })
    expect(wrapper.emitted('committed')).toHaveLength(1)
    expect(wrapper.find('[data-test=pending]').exists()).toBe(false)
  })

  it('offers a folder picker', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.sources, 'files').mockResolvedValue({ items: [], next: null })
    const wrapper = mount(SourceFiles, { props: { source, canEdit: true } })
    await flushPromises()
    expect(wrapper.get('[data-test=folder-input]').attributes()).toHaveProperty('webkitdirectory')
    await pick(wrapper, 'folder-input', [new File(['a'], 'a.pdf'), new File(['b'], 'Thumbs.db')])
    expect(wrapper.get('[data-test=pending-summary]').text()).toContain('1 file ready')
  })

  it('replaces a slot with new bytes and deletes selected slots after a confirmation', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.sources, 'files').mockResolvedValue({ items: [slot('u1', 'taxa.pdf', 'indexed'), slot('u2', 'gammal.doc', 'refused', 'upload.format_deferred')], next: null })
    vi.spyOn(api.sources, 'uploadBatch').mockResolvedValueOnce({ batchId: 'b2', maxFiles: 100, maxBytes: 500 }).mockResolvedValueOnce({ batchId: 'b3', maxFiles: 100, maxBytes: 500 })
    const put = vi.spyOn(api.uploads, 'put').mockResolvedValue({ n: 1, uploadId: 'u1', operation: 'replace', fileName: 'taxa.pdf', bytes: 1, sha256: '' })
    const commit = vi.spyOn(api.uploads, 'commit').mockResolvedValue({ batchId: 'b', jobId: 'j', entries: 1 })
    const wrapper = mount(SourceFiles, { props: { source, canEdit: true }, attachTo: document.body })
    await flushPromises()

    expect(wrapper.text()).toContain('Refused')
    await wrapper.get('[data-test=replace-u1]').trigger('click')
    const replacement = new File(['new'], 'taxa-2027.pdf')
    await pick(wrapper, 'replace-input', [replacement])
    await flushPromises()
    expect(put).toHaveBeenCalledWith('b2', 1, replacement, 'u1')
    expect(commit).toHaveBeenCalledWith('b2', { files: [1], deletes: [] })

    await wrapper.findAll('input[type=checkbox]')[1]!.setValue(true)
    await wrapper.get('[data-test=delete-files]').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('Within 14 days the platform team can bring them back.')
    ;[...document.body.querySelectorAll('button')].filter((b) => b.textContent?.trim() === 'Remove 1 file').at(-1)!.click()
    await flushPromises()
    expect(commit).toHaveBeenLastCalledWith('b3', { files: [], deletes: ['u2'] })
    wrapper.unmount()
  })

  it('lets a platform admin restore a removed file within 14 days (connectors §9.3)', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-platform-admin'] })
    auth.status = 'authenticated'
    const days = (n: number) => new Date(Date.now() - n * 24 * 60 * 60 * 1000).toISOString()
    const files = vi.spyOn(api.sources, 'files').mockImplementation(async (_id, query) => (query?.deleted
      ? { items: [{ ...slot('u3', 'taxa-2025.pdf', 'deleted'), deletedAt: days(3) }, { ...slot('u4', 'gammal.pdf', 'deleted'), deletedAt: days(15) }], next: null }
      : { items: [slot('u1', 'taxa.pdf', 'indexed')], next: null }))
    const restore = vi.spyOn(api.sources, 'restoreFiles').mockResolvedValue({ batchId: 'b', jobId: 'j', entries: 1 })
    const wrapper = mount(SourceFiles, { props: { source, canEdit: true }, attachTo: document.body })
    await flushPromises()
    expect(wrapper.find('[data-test^=restore-]').exists()).toBe(false) // only among the removed files

    await wrapper.get('[data-test=show-deleted]').trigger('click')
    await flushPromises()
    expect(files).toHaveBeenLastCalledWith('s1', expect.objectContaining({ deleted: true }))
    expect(wrapper.find('[data-test=restore-u4]').exists()).toBe(false)
    expect(wrapper.get('[data-test=restore-expired-u4]').text()).toContain('can no longer be restored')

    await wrapper.get('[data-test=restore-u3]').trigger('click')
    await flushPromises()
    ;[...document.body.querySelectorAll('button')].filter((b) => b.textContent?.trim() === 'Restore').at(-1)!.click()
    await flushPromises()
    expect(restore).toHaveBeenCalledWith('s1', ['u3'])
    expect(wrapper.emitted('committed')).toHaveLength(1)
    wrapper.unmount()
  })

  it('offers no restore to a department admin', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-department-admin'] })
    auth.status = 'authenticated'
    vi.spyOn(api.sources, 'files').mockResolvedValue({ items: [{ ...slot('u3', 'taxa-2025.pdf', 'deleted'), deletedAt: new Date().toISOString() }], next: null })
    const wrapper = mount(SourceFiles, { props: { source, canEdit: true } })
    await flushPromises()
    await wrapper.get('[data-test=show-deleted]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-test^=restore-]').exists()).toBe(false)
  })

  it('shows quarantined files as metadata only', async () => {
    vi.spyOn(api.sources, 'quarantine').mockResolvedValue({
      items: [{ id: 'q1', origin: 'upload', name: 'eicar.pdf', url: null, signature: 'Win.Test.EICAR_HDB-1', runId: null, detectedAt: '2026-11-01T10:00:00Z', purgeAfter: '2026-12-01T10:00:00Z' }],
      next: null,
    })
    const wrapper = mount(SourceQuarantine, { props: { sourceId: 's1' } })
    await flushPromises()
    expect(wrapper.text()).toContain('eicar.pdf')
    expect(wrapper.text()).toContain('Win.Test.EICAR_HDB-1')
    expect(wrapper.find('a').exists()).toBe(false) // never a download
  })
})

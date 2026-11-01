import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { Collection, DocumentSummary } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import { button } from '@/test/helpers'
import CollectionView from './CollectionView.vue'

const base: Collection = {
  id: 'c1', slug: 'bygglov', name: 'Bygglov', departmentId: 'd1', classification: 'internal', publishedAt: null, ownerTeamId: null,
  embeddingProfileId: 'p', retrievalSettings: {}, usedBy: [{ id: 'a1', name: 'Bygglovsassistenten', classification: 'internal', published: false, draft: true }],
  updatedAt: '2026-11-01T10:00:00Z', updatedBy: null, version: '7',
}

function document(overrides: Partial<DocumentSummary>): DocumentSummary {
  return {
    id: 'doc', sourceId: 's1', title: 'Altan', url: 'https://www.umea.se/altan', displayName: null, mediaType: 'text/html', parseOutcome: 'ok',
    piiFlagged: false, piiCleared: false, held: false, live: true, sourceModifiedAt: null, fetchedAt: '2026-11-01T02:14:00Z', lastSeenAt: '2026-11-01T02:14:00Z',
    ...overrides,
  }
}

function signIn(roles: string[]) {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ roles })
  auth.status = 'authenticated'
}

describe('Kunskapsbank (admin design §5.5.2)', () => {
  it('shows the state, who uses it, its sources, and documents filtered to personal data', async () => {
    signIn(['rag-editor'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(base)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([
      { id: 's1', collectionId: 'c1', type: 'crawl', name: 'umea.se/bygga', classification: 'internal', state: 'active' },
    ])
    const list = vi.spyOn(api.documents, 'list').mockImplementation(async (q) => ({
      items: q.pii === 'flagged' ? [document({ id: 'p', title: 'Protokoll', piiFlagged: true })] : [document({}), document({ id: 'p', title: 'Protokoll', piiFlagged: true })],
      next: null,
    }))

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()

    expect(wrapper.get('[data-test=state]').text()).toBe('Ej publicerad')
    expect(wrapper.text()).toContain('Ej publicerad: syns bara i Testa.')
    expect(wrapper.get('[data-test=used-by]').text()).toBe('Bygglovsassistenten (utkast)')
    expect(wrapper.find('[data-test=publish]').exists()).toBe(false) // publishing is Manage

    await button(wrapper, 'Källor (1)').trigger('click')
    expect(wrapper.text()).toContain('umea.se/bygga')
    expect(wrapper.text()).toContain('Webbplats')

    await button(wrapper, 'Dokument').trigger('click')
    await flushPromises()
    expect(wrapper.findAll('[data-test=pii]')).toHaveLength(1)
    expect(wrapper.text()).toContain('Altan')
    await wrapper.get('input[type=checkbox]').setValue(true)
    await flushPromises()
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ collectionId: 'c1', pii: 'flagged' }))
    expect(wrapper.text()).not.toContain('Altan')
  })

  it('lets a department admin publish with the version it read', async () => {
    signIn(['rag-department-admin'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(base)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([])
    const publish = vi.spyOn(api.collections, 'publish').mockResolvedValue({ ...base, publishedAt: '2026-11-01T11:00:00Z', version: '8' })

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()
    await wrapper.get('[data-test=publish]').trigger('click')
    await flushPromises()

    expect(publish).toHaveBeenCalledWith('c1', '7')
    expect(wrapper.get('[data-test=state]').text()).toBe('Publicerad')
  })
})

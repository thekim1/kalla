import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { api } from '@/api'
import type { CollectionSetup, Overview } from '@/api/types'
import { createPinia, setActivePinia } from 'pinia'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import { OVERVIEW } from '@/utils/labels'
import OverviewView from './OverviewView.vue'

/** An overview with nothing in it but `overrides`. */
function overviewOf(overrides: Partial<Overview> = {}): Overview {
  return {
    hasCollections: true,
    setup: [],
    needsEmbeddingProfile: false,
    live: [],
    health: {
      departments: [],
      queue: { waiting: 0, oldestQueuedAt: null },
      organisationSync: { lastSuccessAt: null, consecutiveFailures: 0 },
      modelSync: { lastSuccessAt: null, consecutiveFailures: 0 },
    },
    secretExpiry: [],
    deploymentNotices: [],
    ...overrides,
  }
}

function setupOf(overrides: Partial<CollectionSetup>): CollectionSetup {
  return {
    collectionId: 'c1', name: 'Bygglov', sources: 1, firstSyncDone: false, syncRunning: true, testQuestions: 0, evaluated: false, evaluating: false,
    published: false, assistantId: null, assistantPublished: false, available: false, done: false, ...overrides,
  }
}

/** A RouterLink that renders its target, so a spec can read where a link goes. */
const LinkStub = { props: ['to'], template: '<a :data-to="JSON.stringify(to)"><slot /></a>' }

function signIn(overrides: Parameters<typeof bffUser>[0]) {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser(overrides)
  auth.status = 'authenticated'
}

describe('Overview: your access (P0 checklist #9)', () => {
  it('shows the roles, each department by name, and where each department came from', async () => {
    signIn({
      roles: ['rag-viewer', 'offline_access'],
      departmentCodes: ['1000', '3000'],
      departmentSources: [{ code: '1000', source: 'claim' }, { code: '3000', source: 'group' }],
    })
    vi.spyOn(api, 'overview').mockResolvedValue(overviewOf())
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([
      { id: 'd1', name: 'Bygg och miljö', costCenterCode: '1000', isActive: true, syncedAt: '2026-11-01T10:00:00Z', teams: [] },
    ])
    const wrapper = mount(OverviewView, { global: { stubs: { RouterLink: true } } })
    await flushPromises()
    expect(wrapper.get('[data-test=roles]').text()).toBe('Read access')
    const items = wrapper.findAll('[data-test=departments] li').map((li) => li.text())
    expect(items[0]).toContain('Bygg och miljö')
    expect(items[0]).toContain('1000')
    expect(items[0]).toContain('From your account')
    expect(items[1]).toContain('Unknown department (code 3000)')
    expect(items[1]).toContain('Through a member group')
  })

  it('tells a user without a rag-* role to ask for access', () => {
    signIn({ roles: [], departmentCodes: [], departmentSources: [] })
    const wrapper = mount(OverviewView)
    expect(wrapper.text()).toContain('You have no role in the Knowledge platform yet.')
  })

  it('shows Get started for each unfinished collection, and the empty state with Create knowledge base', async () => {
    signIn({ roles: ['rag-editor'], departmentCodes: ['1000'], departmentSources: [{ code: '1000', source: 'claim' }] })
    const overview = vi.spyOn(api, 'overview').mockResolvedValue(overviewOf({ setup: [setupOf({})] }))
    const wrapper = mount(OverviewView, { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } })
    await flushPromises()
    expect(wrapper.get('[data-test=setup]').text()).toContain('Get started: Bygglov')
    expect(wrapper.get('[data-test=step-source]').attributes('data-state')).toBe('done')
    expect(wrapper.get('[data-test=step-sync]').attributes('data-state')).toBe('active')
    expect(wrapper.get('[data-test=step-sync]').text()).toContain('Fetching')
    expect(wrapper.get('[data-test=step-available]').attributes('data-state')).toBe('todo')

    overview.mockResolvedValue(overviewOf({ hasCollections: false }))
    const empty = mount(OverviewView, { global: { stubs: { RouterLink: true } } })
    await flushPromises()
    expect(empty.get('[data-test=empty]').text()).toContain('You have no knowledge bases yet.')
    expect(empty.find('[data-test=create]').exists()).toBe(true)
  })

  it('asks a platform admin to create the embedding profile until one exists, above the empty state', async () => {
    signIn({ roles: ['rag-platform-admin'], departmentCodes: [], departmentSources: [] })
    const overview = vi.spyOn(api, 'overview').mockResolvedValue(overviewOf({ hasCollections: false, needsEmbeddingProfile: true }))
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    const wrapper = mount(OverviewView, { global: { stubs: { RouterLink: true } } })
    await flushPromises()
    const card = wrapper.get('[data-test=needs-profile]')
    expect(card.text()).toContain(OVERVIEW.profileText)
    expect(card.get('[data-test=create-profile]').text()).toContain(OVERVIEW.profileAction)
    expect(wrapper.find('[data-test=empty]').exists()).toBe(true)

    overview.mockResolvedValue(overviewOf({ hasCollections: false }))
    const done = mount(OverviewView, { global: { stubs: { RouterLink: true } } })
    await flushPromises()
    expect(done.find('[data-test=needs-profile]').exists()).toBe(false)
  })
})

describe('Home: Live, Health and Get started (admin design §5.5.1)', () => {
  afterEach(() => window.localStorage.clear())

  function editor() {
    signIn({ roles: ['rag-editor'], departmentCodes: ['1000'], departmentSources: [{ code: '1000', source: 'claim' }] })
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
  }

  const assistant = {
    assistantId: 'a1', name: 'Bygglovsassistenten', departmentId: 'd1', publishedVersion: 3, questions: 42, notFound: 5, thumbsUp: 7, thumbsDown: 2, costUsd: 1.5,
  }

  it('shows a card per published assistant linking to its Statistics tab, and says when nobody asked anything', async () => {
    editor()
    const overview = vi.spyOn(api, 'overview').mockResolvedValue(overviewOf({ live: [assistant, { ...assistant, assistantId: 'a2', name: 'Tyst', questions: 0 }] }))
    const wrapper = mount(OverviewView, { global: { stubs: { RouterLink: LinkStub } } })
    await flushPromises()
    const cards = wrapper.findAll('[data-test=live-card]')
    expect(cards.map((c) => c.find('.font-medium').text())).toEqual(['Bygglovsassistenten', 'Tyst'])
    expect(JSON.parse(cards[0]!.attributes('data-to')!)).toEqual({ name: 'assistant', params: { id: 'a1' }, query: { tab: 'statistics' } })
    expect(cards[0]!.text()).toContain(OVERVIEW.liveVersion(3))
    expect(cards[0]!.get('[data-test=live-questions]').text()).toBe('42')
    expect(cards[0]!.get('[data-test=live-cost]').text()).toContain('1,50')
    expect(wrapper.find('[data-test=live-empty]').exists()).toBe(false)

    overview.mockResolvedValue(overviewOf({ live: [{ ...assistant, questions: 0 }] }))
    const quiet = mount(OverviewView, { global: { stubs: { RouterLink: LinkStub } } })
    await flushPromises()
    expect(quiet.get('[data-test=live-empty]').text()).toBe(OVERVIEW.liveEmpty)

    overview.mockResolvedValue(overviewOf())
    const none = mount(OverviewView, { global: { stubs: { RouterLink: LinkStub } } })
    await flushPromises()
    expect(none.find('[data-test=live]').exists()).toBe(false)
  })

  it('shows the runs per department by outcome, the queue and the syncs, linking Models for platform admins only', async () => {
    editor()
    const health = {
      departments: [{ departmentId: 'd1', departmentName: 'Bygg och miljö', runs: { succeeded: 3, degraded: 1, failed: 0, running: 2 } }],
      queue: { waiting: 4, oldestQueuedAt: new Date(Date.now() - 2 * 3600_000).toISOString() },
      organisationSync: { lastSuccessAt: new Date(Date.now() - 10 * 60_000).toISOString(), consecutiveFailures: 0 },
      modelSync: { lastSuccessAt: null, consecutiveFailures: 3 },
    }
    vi.spyOn(api, 'overview').mockResolvedValue(overviewOf({ health }))
    const wrapper = mount(OverviewView, { global: { stubs: { RouterLink: LinkStub } } })
    await flushPromises()
    const runs = wrapper.get('[data-test=health-runs] li')
    expect(runs.text()).toContain('Bygg och miljö')
    expect(JSON.parse(runs.get('a').attributes('data-to')!)).toEqual({ name: 'collections' })
    expect(runs.get('[data-test=runs-succeeded]').text()).toBe(OVERVIEW.runsSucceeded(3))
    expect(runs.get('[data-test=runs-degraded]').text()).toBe(OVERVIEW.runsDegraded(1))
    expect(runs.find('[data-test=runs-failed]').exists()).toBe(false)
    expect(runs.get('[data-test=runs-running]').text()).toBe(OVERVIEW.runsRunning(2))
    expect(wrapper.get('[data-test=health-queue]').text()).toContain(OVERVIEW.queueWaiting(4))
    expect(wrapper.get('[data-test=health-queue]').text()).toContain(OVERVIEW.queueOldest('2 h ago'))
    const organisation = wrapper.get('[data-test=health-organisation]')
    expect(organisation.text()).toContain(OVERVIEW.lastSuccess('10 min ago'))
    expect(organisation.find('[data-test=sync-failures]').exists()).toBe(false)
    expect(JSON.parse(organisation.get('a').attributes('data-to')!)).toEqual({ name: 'organisation' })
    const models = wrapper.get('[data-test=health-models]')
    expect(models.text()).toContain(OVERVIEW.noSuccess)
    expect(models.get('[data-test=sync-failures]').text()).toBe(OVERVIEW.failures(3))
    expect(models.find('a').exists()).toBe(false)

    signIn({ roles: ['rag-platform-admin'], departmentCodes: [], departmentSources: [] })
    const platform = mount(OverviewView, { global: { stubs: { RouterLink: LinkStub } } })
    await flushPromises()
    expect(JSON.parse(platform.get('[data-test=health-models] a').attributes('data-to')!)).toEqual({ name: 'platform-models' })
    expect(wrapper.text()).not.toContain(OVERVIEW.noRuns)
  })

  it('folds the checklist of a published knowledge base to one line, which unfolds', async () => {
    editor()
    vi.spyOn(api, 'overview').mockResolvedValue(overviewOf({
      setup: [
        setupOf({ collectionId: 'c1', name: 'Personalhandbok', firstSyncDone: true, syncRunning: false, testQuestions: 12, evaluated: true, published: true }),
        setupOf({ collectionId: 'c2', name: 'Bygglov' }),
      ],
    }))
    const wrapper = mount(OverviewView, { global: { stubs: { RouterLink: LinkStub } } })
    await flushPromises()
    const [folded, open] = wrapper.findAll('[data-test=setup]')
    expect(folded!.attributes('data-folded')).toBe('true')
    expect(folded!.text()).toContain('Personalhandbok: 5 of 8 steps done')
    expect(folded!.find('[data-test=step-source]').exists()).toBe(false)
    expect(open!.attributes('data-folded')).toBeUndefined()
    expect(open!.find('[data-test=step-source]').exists()).toBe(true)
    expect(open!.find('[data-test=fold]').exists()).toBe(false)

    await folded!.get('[data-test=unfold]').trigger('click')
    const unfolded = wrapper.findAll('[data-test=setup]')[0]!
    expect(unfolded.attributes('data-folded')).toBeUndefined()
    expect(unfolded.text()).toContain('Get started: Personalhandbok')
    expect(unfolded.get('[data-test=step-publish]').attributes('data-state')).toBe('done')
    await unfolded.get('[data-test=fold]').trigger('click')
    expect(wrapper.findAll('[data-test=setup]')[0]!.attributes('data-folded')).toBe('true')
  })

  it('hides a checklist in this browser and shows the hidden ones again', async () => {
    editor()
    vi.spyOn(api, 'overview').mockResolvedValue(overviewOf({ setup: [setupOf({ collectionId: 'c1', name: 'Bygglov' }), setupOf({ collectionId: 'c2', name: 'Avfall' })] }))
    const wrapper = mount(OverviewView, { global: { stubs: { RouterLink: LinkStub } } })
    await flushPromises()
    expect(wrapper.find('[data-test=show-hidden]').exists()).toBe(false)
    await wrapper.findAll('[data-test=hide]')[0]!.trigger('click')
    expect(wrapper.findAll('[data-test=setup]').map((s) => s.text())).toEqual([expect.stringContaining('Avfall')])
    expect(wrapper.get('[data-test=show-hidden]').text()).toBe(OVERVIEW.showHidden(1))
    expect(JSON.parse(window.localStorage.getItem('ume-home-hidden-setup')!)).toEqual(['c1'])

    // Remembered on the next visit.
    const again = mount(OverviewView, { global: { stubs: { RouterLink: LinkStub } } })
    await flushPromises()
    expect(again.findAll('[data-test=setup]')).toHaveLength(1)
    await again.get('[data-test=show-hidden]').trigger('click')
    expect(again.findAll('[data-test=setup]')).toHaveLength(2)
    expect(again.find('[data-test=show-hidden]').exists()).toBe(false)
    expect(window.localStorage.getItem('ume-home-hidden-setup')).toBeNull()
  })

  it('works without storage: Hide then lasts for the visit', async () => {
    editor()
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied')
    })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied')
    })
    vi.spyOn(api, 'overview').mockResolvedValue(overviewOf({ setup: [setupOf({ collectionId: 'c1' })] }))
    const wrapper = mount(OverviewView, { global: { stubs: { RouterLink: LinkStub } } })
    await flushPromises()
    expect(wrapper.findAll('[data-test=setup]')).toHaveLength(1)
    await wrapper.get('[data-test=hide]').trigger('click')
    expect(wrapper.find('[data-test=setup]').exists()).toBe(false)
    expect(wrapper.get('[data-test=show-hidden]').text()).toBe(OVERVIEW.showHidden(1))
  })
})

describe('Home: the reminder before a client secret expires (admin design §5.11)', () => {
  const L = OVERVIEW.secretExpiry
  const entries: Overview['secretExpiry'] = [
    { which: 'kalla-admin', expiresOn: '2026-12-01' },
    { which: 'open-webui', expiresOn: '2026-11-12' },
  ]
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-11-15T10:00:00Z'))
  })
  afterEach(() => {
    vi.useRealTimers()
    window.localStorage.clear()
  })

  const platformAdmin = () => signIn({ roles: ['rag-platform-admin'], departmentCodes: [], departmentSources: [] })

  it('shows a platform admin a notice per secret at the top, with the date, the days and the steps, and no link to setup', async () => {
    platformAdmin()
    vi.spyOn(api, 'overview').mockResolvedValue(overviewOf({ secretExpiry: entries, needsEmbeddingProfile: true }))
    const wrapper = mount(OverviewView, { global: { stubs: { RouterLink: LinkStub } } })
    await flushPromises()
    const admin = wrapper.get('[data-test=secret-expiry-kalla-admin]')
    expect(admin.get('[data-test=secret-expiry-text]').text()).toBe(L.expires('the admin UI', '1 Dec 2026', 16))
    expect(admin.text()).toContain(L.steps)
    expect(wrapper.get('[data-test=secret-expiry-open-webui] [data-test=secret-expiry-text]').text()).toBe(L.expired('Open WebUI', '12 Nov 2026', 3))
    expect(wrapper.find('[data-test=secret-expiry] a').exists()).toBe(false)
    expect(wrapper.html()).not.toContain('/setup')
    // Above the first-run step and Live.
    expect(wrapper.html().indexOf('data-test="secret-expiry"')).toBeLessThan(wrapper.html().indexOf('data-test="needs-profile"'))
  })

  it('says today and 1 day in plain words', () => {
    expect(L.expires('Open WebUI', '15 Nov 2026', 0)).toContain('(today)')
    expect(L.expires('Open WebUI', '16 Nov 2026', 1)).toContain('(in 1 day)')
    expect(L.expired('Open WebUI', '14 Nov 2026', 1)).toContain('(1 day ago)')
  })

  it('hides a notice in this browser for that date, and shows it again for a new date', async () => {
    platformAdmin()
    const overview = vi.spyOn(api, 'overview').mockResolvedValue(overviewOf({ secretExpiry: entries }))
    let wrapper = mount(OverviewView, { global: { stubs: { RouterLink: LinkStub } } })
    await flushPromises()
    await wrapper.get('[data-test=secret-expiry-kalla-admin] [data-test=secret-expiry-hide]').trigger('click')
    expect(wrapper.find('[data-test=secret-expiry-kalla-admin]').exists()).toBe(false)
    expect(JSON.parse(window.localStorage.getItem('ume-home-hidden-secret-expiry')!)).toEqual(['kalla-admin:2026-12-01'])
    wrapper.unmount()

    wrapper = mount(OverviewView, { global: { stubs: { RouterLink: LinkStub } } })
    await flushPromises()
    expect(wrapper.find('[data-test=secret-expiry-kalla-admin]').exists()).toBe(false)
    expect(wrapper.find('[data-test=secret-expiry-open-webui]').exists()).toBe(true)
    wrapper.unmount()

    overview.mockResolvedValue(overviewOf({ secretExpiry: [{ which: 'kalla-admin', expiresOn: '2026-12-10' }] }))
    wrapper = mount(OverviewView, { global: { stubs: { RouterLink: LinkStub } } })
    await flushPromises()
    expect(wrapper.find('[data-test=secret-expiry-kalla-admin]').exists()).toBe(true)
  })

  it('shows nothing to anyone but a platform admin', async () => {
    signIn({ roles: ['rag-editor'], departmentCodes: ['1000'], departmentSources: [{ code: '1000', source: 'claim' }] })
    vi.spyOn(api, 'overview').mockResolvedValue(overviewOf({ secretExpiry: entries }))
    const wrapper = mount(OverviewView, { global: { stubs: { RouterLink: LinkStub } } })
    await flushPromises()
    expect(wrapper.find('[data-test=secret-expiry]').exists()).toBe(false)
  })
})

describe('Home: the deployment notices (admin design §5.5.1)', () => {
  const L = OVERVIEW.deployment
  const notices: Overview['deploymentNotices'] = [{ code: 'retired_variables', names: ['KALLA_CLAMAV_MODE', 'KALLA_EMBEDDING_MODEL'] }]
  afterEach(() => window.localStorage.clear())
  const platformAdmin = () => signIn({ roles: ['rag-platform-admin'], departmentCodes: [], departmentSources: [] })
  const mountHome = () => mount(OverviewView, { global: { stubs: { RouterLink: LinkStub } } })

  it('names the retired variables the deployment still sets for a platform admin, above the first-run step', async () => {
    platformAdmin()
    vi.spyOn(api, 'overview').mockResolvedValue(overviewOf({ deploymentNotices: notices, needsEmbeddingProfile: true }))
    const wrapper = mountHome()
    await flushPromises()
    const notice = wrapper.get('[data-test=deployment-notice-retired_variables]')
    expect(notice.get('[data-test=deployment-notice-title]').text()).toBe(L.retired_variables.title(2))
    expect(notice.findAll('[data-test=deployment-notice-names] li').map((li) => li.text())).toEqual(['KALLA_CLAMAV_MODE', 'KALLA_EMBEDDING_MODEL'])
    expect(notice.text()).toContain(L.retired_variables.text)
    expect(wrapper.html().indexOf('data-test="deployment-notices"')).toBeLessThan(wrapper.html().indexOf('data-test="needs-profile"'))
  })

  it('says one variable in the singular', () => {
    expect(L.retired_variables.title(1)).toBe('The deployment sets a variable that this version no longer reads')
  })

  it('hides the notice in this browser for those names, and shows it again when the names change', async () => {
    platformAdmin()
    const overview = vi.spyOn(api, 'overview').mockResolvedValue(overviewOf({ deploymentNotices: notices }))
    let wrapper = mountHome()
    await flushPromises()
    await wrapper.get('[data-test=deployment-notice-hide]').trigger('click')
    expect(wrapper.find('[data-test=deployment-notices]').exists()).toBe(false)
    wrapper.unmount()

    wrapper = mountHome()
    await flushPromises()
    expect(wrapper.find('[data-test=deployment-notices]').exists()).toBe(false)
    wrapper.unmount()

    overview.mockResolvedValue(overviewOf({ deploymentNotices: [{ code: 'retired_variables', names: ['FORS_ORGANISATION_NAME'] }] }))
    wrapper = mountHome()
    await flushPromises()
    expect(wrapper.get('[data-test=deployment-notice-names]').text()).toBe('FORS_ORGANISATION_NAME')
  })

  it('shows nothing for a code it has no text for, and nothing to anyone but a platform admin', async () => {
    platformAdmin()
    const overview = vi.spyOn(api, 'overview').mockResolvedValue(overviewOf({ deploymentNotices: [{ code: 'from_a_newer_api', names: ['x'] }] }))
    let wrapper = mountHome()
    await flushPromises()
    expect(wrapper.find('[data-test=deployment-notices]').exists()).toBe(false)
    wrapper.unmount()

    signIn({ roles: ['rag-editor'], departmentCodes: ['1000'], departmentSources: [{ code: '1000', source: 'claim' }] })
    overview.mockResolvedValue(overviewOf({ deploymentNotices: notices }))
    wrapper = mountHome()
    await flushPromises()
    expect(wrapper.find('[data-test=deployment-notices]').exists()).toBe(false)
  })
})

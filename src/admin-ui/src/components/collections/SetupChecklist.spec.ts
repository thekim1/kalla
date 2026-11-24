import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { CollectionSetup } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import { SETUP } from '@/utils/labels'
import SetupChecklist from './SetupChecklist.vue'

/** A knowledge base whose assistant is published, offered to an app (`available`) or not. */
function setupOf(overrides: Partial<CollectionSetup> = {}): CollectionSetup {
  return {
    collectionId: 'c1', name: 'Bygglov', sources: 1, firstSyncDone: true, syncRunning: false, testQuestions: 3, evaluated: true, evaluating: false,
    published: true, assistantId: 'a1', assistantPublished: true, available: false, done: false, ...overrides,
  }
}

/** A RouterLink that renders its target, so a spec can read where a link goes. */
const LinkStub = { props: ['to'], template: '<a :data-to="JSON.stringify(to)"><slot /></a>' }

function mountWith(openWebUi: boolean, setup: CollectionSetup) {
  setActivePinia(createPinia())
  useAuthStore().user = bffUser({ roles: ['rag-editor'], openWebUi: { enabled: openWebUi } })
  return mount(SetupChecklist, { props: { setup }, global: { stubs: { RouterLink: LinkStub } } })
}

const target = (wrapper: ReturnType<typeof mountWith>) => JSON.parse(wrapper.get('[data-test=step-available] a').attributes('data-to')!)

describe('Get started: the last step (admin design §5.4)', () => {
  it('makes the assistant available in Open WebUI while the deployment runs it', () => {
    const wrapper = mountWith(true, setupOf())
    const step = wrapper.get('[data-test=step-available]')
    expect(step.text()).toContain(SETUP.steps.available)
    expect(step.attributes('data-state')).toBe('todo')
    expect(target(wrapper)).toEqual({ name: 'assistant', params: { id: 'a1' } })

    expect(mountWith(true, setupOf({ available: true })).get('[data-test=step-available]').attributes('data-state')).toBe('done')
  })

  it('without Open WebUI names an app with a key, opens the Audience tab, and is done once an app offers the assistant', () => {
    const wrapper = mountWith(false, setupOf())
    const step = wrapper.get('[data-test=step-available]')
    expect(step.text()).toContain(SETUP.steps.availableApp)
    expect(wrapper.text()).not.toContain('Open WebUI')
    expect(step.attributes('data-state')).toBe('todo')
    expect(target(wrapper)).toEqual({ name: 'assistant', params: { id: 'a1' }, query: { tab: 'access' } })

    expect(mountWith(false, setupOf({ available: true })).get('[data-test=step-available]').attributes('data-state')).toBe('done')
    // No assistant yet: the step opens the assistants list, as the assistant steps do.
    expect(target(mountWith(false, setupOf({ assistantId: null, assistantPublished: false })))).toEqual({ name: 'assistants' })
  })
})

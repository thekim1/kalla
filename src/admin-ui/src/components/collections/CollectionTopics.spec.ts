import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { api } from '@/api'
import { TOPICS } from '@/utils/labels'
import CollectionTopics from './CollectionTopics.vue'

/** A RouterLink that renders its target, so a spec can read where a link goes. */
const LinkStub = { props: ['to'], template: '<a :data-to="JSON.stringify(to)"><slot /></a>' }

function mountWith(sources?: { id: string; name: string }[]) {
  vi.spyOn(api.collections, 'topics').mockResolvedValue({ collectionId: 'c1', topics: [] })
  return mount(CollectionTopics, { props: { collectionId: 'c1', sources }, global: { stubs: { RouterLink: LinkStub } } })
}

describe('Topics tab (admin design §5.5.2, #929)', () => {
  it('tells the owner where the rules are edited, links each source and shows how to write one', async () => {
    const wrapper = mountWith([{ id: 's1', name: 'umea.se' }, { id: 's2', name: 'Files' }])
    await flushPromises()

    const help = wrapper.get('[data-test=topics-help]')
    expect(help.text()).toContain(TOPICS.editRules)
    expect(help.findAll('[data-test=topics-source]').map((a) => [a.text(), JSON.parse(a.attributes('data-to')!)])).toEqual([
      ['umea.se', { name: 'source', params: { id: 's1' } }],
      ['Files', { name: 'source', params: { id: 's2' } }],
    ])
    expect(help.text()).toContain(TOPICS.howTitle)
    // The example is valid rules JSON, as the Topics field takes it.
    expect(JSON.parse(TOPICS.example)).toEqual([
      { match: 'https://www.umea.se/bygga-bo-och-miljo/bygglov/', on: 'path', tags: ['bygglov'] },
      { match: '/kommun-och-politik/', on: 'path', tags: ['politik'] },
    ])
    expect(wrapper.text()).toContain(TOPICS.none)
  })

  it('shows a department the knowledge base is shared with the topics only, with no help for rules it cannot edit', async () => {
    const wrapper = mountWith()
    await flushPromises()
    expect(wrapper.find('[data-test=topics-help]').exists()).toBe(false)
  })
})

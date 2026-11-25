import { describe, expect, it } from 'vitest'
import { mount, RouterLinkStub } from '@vue/test-utils'
import type { MapRow } from '@/api/types'
import { MAP } from '@/utils/labels'
import MapDrawing from './MapDrawing.vue'

const row: MapRow = {
  collection: {
    id: 'c1', name: 'Bygglov', departmentId: 'd1', departmentName: 'Stadsbyggnad', shared: false, sharedWith: 'department', classification: 'internal',
    published: true, contactName: null, contactEmail: null, sources: [{ id: 's1', name: 'umea.se/bygga', type: 'crawl' }, { id: 's2', name: 'Blanketter', type: 'upload' }],
  },
  assistant: { id: 'a1', name: 'Bygglovsassistenten', state: 'active', publishedVersion: 3, draftOnly: false, topics: [], contactName: null, contactEmail: null },
  audience: null,
}

describe('MapDrawing (admin design §5.5.6, B-67)', () => {
  it('lists each column with its links and the relations as text, and highlights what a focused item is connected to', async () => {
    const wrapper = mount(MapDrawing, { props: { departmentName: 'Stadsbyggnad', rows: [row] }, global: { stubs: { RouterLink: RouterLinkStub } } })
    const column = (stage: string) => wrapper.get(`[data-test=map-column-${stage}]`)
    expect(column('source').get('h3').text()).toBe(MAP.drawing.columns.source)
    expect(column('source').findAll('[data-test=map-relation]').map((r) => r.text())).toEqual([MAP.drawing.relations.source!('Bygglov'), MAP.drawing.relations.source!('Bygglov')])
    expect(column('collection').get('[data-test=map-relation]').text()).toBe(MAP.drawing.relations.collection!('Bygglovsassistenten'))
    expect(column('audience').text()).toContain(MAP.drawing.noneInColumn)
    expect(wrapper.findAllComponents(RouterLinkStub).map((l) => l.props('to'))).toContainEqual({ name: 'assistant', params: { id: 'a1' } })
    // The lines are for the eye only.
    expect(wrapper.get('[data-test=map-lines]').attributes('aria-hidden')).toBe('true')

    const nodes = () => wrapper.findAll('[data-test=map-node]')
    expect(nodes().some((n) => n.classes().includes('!bg-accent-soft'))).toBe(false)
    await nodes()[0]!.trigger('focusin')
    expect(nodes().map((n) => n.classes().includes('!bg-accent-soft'))).toEqual([true, false, true, true])
    await nodes()[0]!.trigger('focusout')
    expect(nodes().some((n) => n.classes().includes('!bg-accent-soft'))).toBe(false)
  })

  it('lets the keyboard hold a channel, wraps long names, and adds no regions per column (#1083)', async () => {
    const long = 'https://www.umea.se/bygga-bo-och-miljo/bygglov/ansok-om-bygglov'
    const withChannel: MapRow = {
      ...row, collection: { ...row.collection, sources: [{ id: 's1', name: long, type: 'crawl' }] },
      audience: { clientId: 'k1', name: 'Open WebUI (pilot)', kind: 'open-webui' },
    }
    const wrapper = mount(MapDrawing, { props: { departmentName: 'Stadsbyggnad', rows: [withChannel] }, global: { stubs: { RouterLink: RouterLinkStub } } })
    expect(wrapper.findAll('section')).toHaveLength(1) // the card only
    const channel = wrapper.get('[data-test=map-node-button]')
    expect(channel.text()).toBe('Open WebUI (pilot)')
    await channel.trigger('click')
    expect(channel.attributes('aria-pressed')).toBe('true')
    const lit = () => wrapper.findAll('[data-test=map-node]').map((n) => n.classes().includes('!bg-accent-soft'))
    expect(lit()).toEqual([true, true, true, true]) // the channel and everything that feeds it
    await wrapper.findAll('[data-test=map-node]')[0]!.trigger('mouseleave')
    expect(lit()).toEqual([true, true, true, true]) // held until clicked again
    await channel.trigger('click')
    expect(channel.attributes('aria-pressed')).toBe('false')
    await wrapper.findAll('[data-test=map-node]')[3]!.trigger('focusout')
    expect(lit()).toEqual([false, false, false, false]) // let go, and focus gone
    expect(wrapper.get('[data-test=map-node]').classes()).toContain('[overflow-wrap:anywhere]')
  })
})

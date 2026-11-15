import { describe, expect, it, vi } from 'vitest'
import { mount, RouterLinkStub } from '@vue/test-utils'
import { h } from 'vue'
import DataTable from './DataTable.vue'

const push = vi.hoisted(() => vi.fn())
vi.mock('vue-router', async (original) => ({ ...(await original<typeof import('vue-router')>()), useRouter: () => ({ push }) }))

interface Row { id: string; name: string; runId: string }
const rows: Row[] = [{ id: 'c1', name: 'Blanketter', runId: 'r1' }, { id: 'c2', name: 'umea.se', runId: 'r2' }]
const columns = [{ key: 'name', label: 'Name' }, { key: 'run', label: 'Last fetch' }, { key: 'note', label: 'Note' }]
const slots = {
  'cell-name': ({ row }: { row: Row }) => row.name,
  'cell-run': ({ row }: { row: Row }) => h(RouterLinkStub, { to: { name: 'run', params: { id: row.runId } }, 'data-test': `run-${row.id}` }, () => 'run'),
  'cell-note': () => 'note',
}

describe('DataTable rows (admin design §5.5.2 "Lists open as links")', () => {
  it('makes the first cell of a page row a real link, and a click elsewhere on the row opens the page once', async () => {
    push.mockClear()
    const wrapper = mount(DataTable<Row>, {
      props: { columns, rows, rowKey: (r: Row) => r.id, caption: 'Knowledge bases', rowTo: (r: Row) => ({ name: 'collection', params: { id: r.id } }) },
      slots,
      global: { stubs: { RouterLink: RouterLinkStub } },
    })

    const links = wrapper.findAll('[data-test=row-link]')
    expect(links.map((l) => l.element.tagName)).toEqual(['A', 'A'])
    expect(links[0]!.text()).toBe('Blanketter')
    expect(wrapper.findAllComponents(RouterLinkStub).filter((l) => l.attributes('data-test') === 'row-link').map((l) => l.props('to'))).toEqual([
      { name: 'collection', params: { id: 'c1' } }, { name: 'collection', params: { id: 'c2' } },
    ])
    expect(wrapper.find('tbody button').exists()).toBe(false)

    // The link navigates by itself (no second navigation by the row), as does another link in the row.
    await links[0]!.trigger('click')
    await wrapper.get('[data-test=run-c1]').trigger('click')
    expect(push).not.toHaveBeenCalled()

    await wrapper.findAll('tbody tr')[1]!.findAll('td')[1]!.trigger('click')
    expect(push).toHaveBeenCalledTimes(1)
    expect(push).toHaveBeenCalledWith({ name: 'collection', params: { id: 'c2' } })
    expect(wrapper.emitted('row-click')).toBeUndefined()
  })

  it('keeps a button and the row-click event for rows that open a drawer', async () => {
    push.mockClear()
    const wrapper = mount(DataTable<Row>, {
      props: { columns, rows, rowKey: (r: Row) => r.id, caption: 'Keys', clickable: true },
      slots,
      global: { stubs: { RouterLink: RouterLinkStub } },
    })

    expect(wrapper.find('[data-test=row-link]').exists()).toBe(false)
    await wrapper.findAll('tbody tr')[0]!.get('th button').trigger('click')
    await wrapper.findAll('tbody tr')[1]!.findAll('td')[1]!.trigger('click')
    expect(wrapper.emitted('row-click')).toEqual([[rows[0]], [rows[1]]])
    expect(push).not.toHaveBeenCalled()
  })
})

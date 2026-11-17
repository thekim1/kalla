import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { Collection, Department } from '@/api/types'
import MoveDepartmentDrawer from './MoveDepartmentDrawer.vue'

const department = (id: string, name: string, teams: Department['teams'] = []): Department =>
  ({ id, name, costCenterCode: id, isActive: true, syncedAt: '', teams }) as Department

describe('Change department (#811, admin design §3.2)', () => {
  it('asks for an owner team when the new department has several, and offers none only when it has no team', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([
      department('old', 'Fritid'),
      department('two', 'Skola', [{ id: 'a', name: 'Grundskola', code: null, isActive: true }, { id: 'b', name: 'Gymnasium', code: null, isActive: true }]),
      department('none', 'Kultur'),
    ])
    const wrapper = mount(MoveDepartmentDrawer, {
      props: { open: true, kind: 'assistant', id: 'a1', departmentId: 'old', version: '3' },
      global: { stubs: { GatewayKeyPicker: true, Teleport: true } },
    })
    await flushPromises()
    await wrapper.get('#move-department-target').setValue('two')
    await flushPromises()
    expect(wrapper.get('[data-test=check]').attributes('aria-disabled')).toBe('true')
    await wrapper.get('#move-department-team').setValue('b')
    expect(wrapper.get('[data-test=check]').attributes('aria-disabled')).toBeUndefined()

    await wrapper.get('#move-department-target').setValue('none')
    await flushPromises()
    expect(wrapper.get('#move-department-team').text()).toContain('No owner team')
    expect(wrapper.get('[data-test=check]').attributes('aria-disabled')).toBeUndefined()
  })

  it('checks the impact, lists the shares it adds, moves with the token and drops the impact when a field changes', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([
      department('old', 'Fritid'),
      department('new', 'Vuxenutbildning', [{ id: 't1', name: 'Komvux', code: null, isActive: true }]),
      department('other', 'Skola'),
    ])
    const impact = vi.spyOn(api.collections, 'moveImpact').mockResolvedValue({
      shares: [{ collectionId: 'c1', collection: 'Vuxen', departmentId: 'old', department: 'Fritid' }], sourcesOnDepartmentPipelines: [], ownerTeamId: 't1', impactToken: 'tok',
      brokenLinks: [{ assistantId: 'a9', assistant: 'Kundtjänst skola', department: 'Skola', collectionId: 'c1', collection: 'Vuxen' }],
      scopedKeys: [{ keyId: 'k1', key: 'skolportal', department: 'Skola' }],
    })
    const move = vi.spyOn(api.collections, 'move').mockResolvedValue({ id: 'c1', departmentId: 'new' } as Collection)

    const wrapper = mount(MoveDepartmentDrawer, {
      props: { open: true, kind: 'collection', id: 'c1', departmentId: 'old', version: '7' },
      global: { stubs: { GatewayKeyPicker: true, Teleport: true } },
    })
    await flushPromises()

    // The current department is not a target.
    const targets = [...wrapper.get('#move-department-target').findAll('option')].map((o) => o.text())
    expect(targets).not.toContain('Fritid')
    expect(targets).toEqual(expect.arrayContaining(['Vuxenutbildning', 'Skola']))

    // Its only team is preselected.
    await wrapper.get('#move-department-target').setValue('new')
    await flushPromises()
    expect((wrapper.get('#move-department-team').element as HTMLSelectElement).value).toBe('t1')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(impact).toHaveBeenLastCalledWith('c1', { departmentId: 'new', ownerTeamId: 't1', gatewayKeyId: null, version: '7' })
    expect(wrapper.get('[data-test=impact]').text()).toContain('Vuxen with Fritid')
    expect(wrapper.get('[data-test=impact]').text()).toContain('Kundtjänst skola (Skola) → Vuxen')
    expect(wrapper.get('[data-test=impact]').text()).toContain('skolportal (Skola)')
    expect(move).not.toHaveBeenCalled()

    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(move).toHaveBeenCalledWith('c1', { departmentId: 'new', ownerTeamId: 't1', gatewayKeyId: null, version: '7', impactToken: 'tok' })
    expect(wrapper.emitted('moved-collection')?.[0]).toEqual([{ id: 'c1', departmentId: 'new' }, 'Vuxenutbildning'])
    expect(wrapper.emitted('update:open')?.at(-1)).toEqual([false])
  })

  it('shows the sources that block the move and offers no Move, and a changed field asks for a new check', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([department('old', 'Fritid'), department('new', 'Vuxenutbildning'), department('x', 'Skola')])
    vi.spyOn(api.collections, 'moveImpact').mockResolvedValue({ shares: [], sourcesOnDepartmentPipelines: ['umea.se vuxen (Fritids pipeline)'], ownerTeamId: null, brokenLinks: [], scopedKeys: [], impactToken: null })
    const wrapper = mount(MoveDepartmentDrawer, {
      props: { open: true, kind: 'collection', id: 'c1', departmentId: 'old', version: '7' },
      global: { stubs: { GatewayKeyPicker: true, Teleport: true } },
    })
    await flushPromises()
    await wrapper.get('#move-department-target').setValue('new')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.get('[data-test=impact]').text()).toContain('umea.se vuxen (Fritids pipeline)')
    expect(wrapper.find('[data-test=move]').exists()).toBe(false)

    await wrapper.get('#move-department-target').setValue('x')
    await flushPromises()
    expect(wrapper.find('[data-test=impact]').exists()).toBe(false)
    expect(wrapper.find('[data-test=check]').exists()).toBe(true)
  })
})

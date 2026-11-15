import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { api } from '@/api'
import type { CostReport, DepartmentCostRow } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import { sumTotals } from '@/components/costs/costs'
import { formatUsdCost } from '@/utils/format'
import { COSTS } from '@/utils/labels'
import { COST_MESSAGES } from '@/utils/messages'
import CostsView from './CostsView.vue'

function row(overrides: Partial<DepartmentCostRow>): DepartmentCostRow {
  return {
    period: '2026-11-10', departmentId: 'd1', departmentName: 'Bygg', kind: 'assistant', objectId: 'a1', objectName: 'Bygglovsguiden', deleted: false,
    step: 'chat-answer', clientId: 'c1', clientName: 'Open WebUI (pilot)', calls: 1, inputTokens: 100, cachedInputTokens: 0, outputTokens: 20, costUsd: 0,
    estimatedCostUsd: 0, estimatedCalls: 0, ...overrides,
  }
}

function report(rows: DepartmentCostRow[]): CostReport {
  return { from: '2026-10-14', to: '2026-11-12', granularity: 'day', rows, totals: sumTotals(rows) }
}

async function mountView(path = '/costs') {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ roles: ['rag-department-admin'] })
  auth.status = 'authenticated'
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/costs', name: 'costs', component: CostsView }] })
  await router.push(path)
  const wrapper = mount(CostsView, { global: { plugins: [router] } })
  await flushPromises()
  return wrapper
}

afterEach(() => vi.restoreAllMocks())

describe('Costs (admin design §8.6; B-41)', () => {
  it('shows one row per assistant and knowledge base, highest first, with steps, department totals, the CSV link and the notes', async () => {
    const get = vi.spyOn(api.costs, 'report').mockResolvedValue(report([
      row({ costUsd: 0.002 }),
      row({ step: 'query-embed', costUsd: 0.0001 }),
      row({ kind: 'collection', objectId: 'k1', objectName: 'Bygglov', step: 'embed', clientId: null, clientName: null, costUsd: 0.05 }),
      row({ objectId: 'gone', objectName: null, deleted: true, estimatedCostUsd: 0.0003, estimatedCalls: 2 }),
      row({ departmentId: 'd2', departmentName: 'Skola', objectId: 'a2', objectName: 'Skolguiden', costUsd: 0.001 }),
    ]))
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([{ id: 'd1', name: 'Bygg' }, { id: 'd2', name: 'Skola' }] as never)
    const wrapper = await mountView()

    expect(get).toHaveBeenCalledWith(expect.objectContaining({ granularity: 'day' }))
    const departments = wrapper.findAll('[data-test=costs-department]')
    expect(departments).toHaveLength(2)
    const bygg = departments[0]!
    const objects = bygg.findAll('tbody tr').map((r) => r.text())
    expect(objects[0]).toContain('Bygglov') // the knowledge base costs most
    expect(objects[0]).toContain(COSTS.kindCollection)
    expect(objects[1]).toContain('Bygglovsguiden')
    expect(objects[2]).toContain(COSTS.deleted('gone'))
    expect(bygg.get('[data-test=costs-department-total]').text()).toContain(formatUsdCost(0.05 + 0.002 + 0.0001 + 0.0003))
    expect(wrapper.get('[data-test=costs-total]').text()).toContain(formatUsdCost(0.05 + 0.002 + 0.0001 + 0.0003 + 0.001))

    // Expanded by step.
    expect(wrapper.find('[data-test=costs-step-a1-query-embed]').exists()).toBe(false)
    await wrapper.get('[data-test=costs-toggle-a1]').trigger('click')
    expect(wrapper.get('[data-test=costs-step-a1-query-embed]').text()).toContain('Query embedding')
    expect(wrapper.get('[data-test=costs-step-a1-chat-answer]').text()).toContain(formatUsdCost(0.002))

    expect(wrapper.get('[data-test=costs-estimated-note]').text()).toBe(COST_MESSAGES.estimatedNote('2'))
    expect(wrapper.get('[data-test=costs-gateway-note]').text()).toBe(COST_MESSAGES.gatewayRecord)
  })

  it('takes the department from the link, asks again on a filter change, and the CSV link carries the filters', async () => {
    const get = vi.spyOn(api.costs, 'report').mockResolvedValue(report([]))
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([{ id: 'd1', name: 'Bygg' }] as never)
    const wrapper = await mountView('/costs?departmentId=d1')

    expect(get).toHaveBeenLastCalledWith(expect.objectContaining({ departmentId: 'd1' }))
    expect(wrapper.text()).toContain(COST_MESSAGES.empty)

    await wrapper.findAll('select')[1]!.setValue('collection')
    await wrapper.findAll('button').find((b) => b.text() === COSTS.granularities.month)!.trigger('click')
    await flushPromises()
    expect(get).toHaveBeenLastCalledWith(expect.objectContaining({ departmentId: 'd1', kind: 'collection', granularity: 'month' }))
    const href = wrapper.get('[data-test=costs-csv]').attributes('href')!
    const url = new URL(href, 'https://admin.example')
    expect(url.pathname).toBe('/api/costs')
    expect(Object.fromEntries(url.searchParams)).toEqual(expect.objectContaining({ format: 'csv', departmentId: 'd1', kind: 'collection', granularity: 'month' }))
    expect(url.searchParams.get('from')).toMatch(/^\d{4}-\d{2}-\d{2}$/)
  })

  it('labels every filter above its field, the same layout as Feedback', async () => {
    vi.spyOn(api.costs, 'report').mockResolvedValue(report([]))
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([] as never)
    const wrapper = await mountView('/costs')

    const form = wrapper.get('form[role=search]')
    const fields = form.findAll('input, select')
    expect(fields).toHaveLength(4)
    for (const f of fields) expect(form.find(`label[for="${f.attributes('id')}"]`).exists()).toBe(true)
  })
})

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
import { COSTS, FILTERS } from '@/utils/labels'
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

    expect(get).toHaveBeenCalledWith(expect.objectContaining({ granularity: 'month' }))
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

  it('takes the department from the link, asks again on a filter change, and Export carries the filters', async () => {
    const get = vi.spyOn(api.costs, 'report').mockResolvedValue(report([]))
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([{ id: 'd1', name: 'Bygg' }] as never)
    const wrapper = await mountView('/costs?departmentId=d1')

    expect(get).toHaveBeenLastCalledWith(expect.objectContaining({ departmentId: 'd1' }))
    expect(wrapper.text()).toContain(COST_MESSAGES.empty)
    // The department chip shows the value from the link, set and removable.
    expect(wrapper.get('[data-test="chip-Department"] [data-test=chip-remove]').attributes('aria-label')).toBe(FILTERS.remove('Department'))

    await wrapper.get('[data-test="chip-Kind"] select').setValue('collection')
    await flushPromises()
    expect(get).toHaveBeenLastCalledWith(expect.objectContaining({ departmentId: 'd1', kind: 'collection', granularity: 'month' }))
    const url = new URL(wrapper.get('[data-test=costs-csv-day]').attributes('href')!, 'https://admin.example')
    expect(url.pathname).toBe('/api/costs')
    expect(Object.fromEntries(url.searchParams)).toEqual(expect.objectContaining({ format: 'csv', departmentId: 'd1', kind: 'collection', granularity: 'day' }))
    expect(url.searchParams.get('from')).toMatch(/^\d{4}-\d{2}-\d{2}$/)
  })

  it('asks by month for the page, and has Export by day and by month in the header (#710; §5.12 Filters)', async () => {
    const get = vi.spyOn(api.costs, 'report').mockResolvedValue(report([]))
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([] as never)
    const wrapper = await mountView('/costs')
    const granularity = (t: string) => new URL(wrapper.get(`[data-test=${t}]`).attributes('href')!, 'https://admin.example').searchParams.get('granularity')

    expect(get).toHaveBeenCalledTimes(1)
    expect(get).toHaveBeenLastCalledWith(expect.objectContaining({ granularity: 'month' }))
    const exportGroup = wrapper.get('header [data-test=costs-export]')
    expect(exportGroup.attributes('aria-label')).toBe(COSTS.export)
    expect([granularity('costs-csv-day'), granularity('costs-csv-month')]).toEqual(['day', 'month'])
    expect(wrapper.get('[data-test=costs-csv-day]').attributes('aria-label')).toBe(COSTS.exportBy(COSTS.byDay))
  })

  it('downloads a range over 400 days by month only, and says so', async () => {
    vi.spyOn(api.costs, 'report').mockResolvedValue(report([]))
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([] as never)
    const wrapper = await mountView('/costs?period=custom&from=2024-01-01&to=2025-06-30')

    expect(wrapper.find('[data-test=costs-csv-day]').exists()).toBe(false)
    expect(wrapper.find('[data-test=costs-csv-month]').exists()).toBe(true)
    expect(wrapper.get('[data-test=costs-csv-month-only]').text()).toBe(COSTS.csvMonthOnly)
  })

  it('keeps the filters in the address, so a reload opens the same view, and Clear filters takes them out', async () => {
    const get = vi.spyOn(api.costs, 'report').mockResolvedValue(report([]))
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([] as never)
    const wrapper = await mountView('/costs?period=7d&kind=assistant')
    expect(get).toHaveBeenLastCalledWith(expect.objectContaining({ kind: 'assistant' }))
    expect((wrapper.get('[data-test="chip-Period"] select').element as HTMLSelectElement).value).toBe('7d')
    const router = (wrapper.vm as unknown as { $router: { currentRoute: { value: { query: Record<string, string> } } } }).$router
    await wrapper.get('[data-test=clear-filters]').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({})
    expect(get).toHaveBeenLastCalledWith(expect.objectContaining({ kind: undefined }))
    expect(wrapper.get('[data-test=filter-count]').attributes('aria-live')).toBe('polite')
  })
})

import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { api } from '@/api'
import type { CostRow, GatewayKey, ObjectCosts } from '@/api/types'
import { formatUsdCost } from '@/utils/format'
import { COSTS } from '@/utils/labels'
import { COST_MESSAGES } from '@/utils/messages'
import CostCard from './CostCard.vue'

function row(overrides: Partial<CostRow>): CostRow {
  return {
    period: '2026-11-10', step: 'chat-answer', clientId: 'c1', clientName: 'Open WebUI (pilot)', calls: 1, inputTokens: 100, cachedInputTokens: 0, outputTokens: 20,
    costUsd: 0, estimatedCostUsd: 0, estimatedCalls: 0, ...overrides,
  }
}

function costs(rows: CostRow[]): ObjectCosts {
  const sum = (k: keyof CostRow) => rows.reduce((t, r) => t + (r[k] as number), 0)
  return {
    from: '2026-10-14', to: '2026-11-12', granularity: 'day', rows,
    totals: {
      calls: sum('calls'), inputTokens: sum('inputTokens'), cachedInputTokens: sum('cachedInputTokens'), outputTokens: sum('outputTokens'), costUsd: sum('costUsd'),
      estimatedCostUsd: sum('estimatedCostUsd'), estimatedCalls: sum('estimatedCalls'),
    },
  }
}

function gatewayKey(id: string, usedBy: number): GatewayKey {
  return {
    id, name: id, teamId: 't1', teamName: null, departmentId: 'd1', status: 'Active', ownerActive: true, expiresAt: null, allowedModels: [], allowedResidencies: [],
    piiPolicy: 'Off', contentGuardPolicy: 'Off', requestsPerMinute: null, tokensPerMinute: null, maxConcurrentRequests: null, maxOutputTokens: null,
    hasSystemPrompt: false, usable: true, selectable: true, reason: null, syncedAt: '2026-11-12T10:00:00Z', usedBy,
  }
}

const mountCard = (props: InstanceType<typeof CostCard>['$props']) => mount(CostCard, { props, global: { stubs: { RouterLink: RouterLinkStub } } })

afterEach(() => vi.restoreAllMocks())

describe('Cost card (admin design §8.6; B-41)', () => {
  it("shows an assistant's total with the gateway's and the estimated part apart, by step and by client, and the test panel by name", async () => {
    const source = vi.fn().mockResolvedValue(costs([
      row({ costUsd: 0.002, calls: 2 }),
      row({ period: '2026-11-11', estimatedCostUsd: 0.0005, estimatedCalls: 1 }),
      row({ step: 'query-embed', costUsd: 0.0001, outputTokens: 0 }),
      row({ clientId: 'c9', clientName: 'admin-console', costUsd: 0.0004 }),
    ]))
    const wrapper = mountCard({ kind: 'assistant', source })
    await flushPromises()

    expect(source).toHaveBeenCalledWith(expect.objectContaining({ granularity: 'day' }))
    const [query] = source.mock.calls[0]! as [{ from: string; to: string }]
    expect((Date.parse(query.to) - Date.parse(query.from)) / 86_400_000).toBe(29) // the last 30 days, today included
    expect(wrapper.get('[data-test=cost-total]').text()).toBe(formatUsdCost(0.002 + 0.0005 + 0.0001 + 0.0004))
    const parts = wrapper.get('[data-test=cost-parts]').text()
    expect(parts).toContain(`${COSTS.gateway}: ${formatUsdCost(0.002 + 0.0001 + 0.0004)}`)
    expect(parts).toContain(`${COSTS.estimated}: ${formatUsdCost(0.0005)}`)

    const steps = wrapper.get('[data-test=cost-steps]').text()
    expect(steps).toContain('Answer')
    expect(steps).toContain('Query embedding')
    expect(steps.indexOf('Answer')).toBeLessThan(steps.indexOf('Query embedding')) // highest cost first
    const clients = wrapper.get('[data-test=cost-clients]').text()
    expect(clients).toContain('Open WebUI (pilot)')
    expect(clients).toContain(COSTS.testPanel)
    expect(clients).not.toContain('admin-console')
    expect(wrapper.get('[data-test=cost-estimated-note]').text()).toBe(COST_MESSAGES.estimatedNote('1'))
    expect(wrapper.find('[data-test=cost-split]').exists()).toBe(false)

    // By month: asked again with the new granularity.
    await wrapper.findAll('button').find((b) => b.text() === COSTS.granularities.month)!.trigger('click')
    await flushPromises()
    expect(source).toHaveBeenLastCalledWith(expect.objectContaining({ granularity: 'month' }))
  })

  it("splits a knowledge base's cost into indexing and evaluation and links to the Costs page", async () => {
    const source = vi.fn().mockResolvedValue(costs([
      row({ step: 'embed', clientId: null, clientName: null, costUsd: 0.02 }),
      row({ step: 'eval-judge', clientId: null, clientName: null, costUsd: 0.003 }),
      row({ step: 'eval-generate', clientId: null, clientName: null, estimatedCostUsd: 0.001, estimatedCalls: 1 }),
    ]))
    const wrapper = mountCard({ kind: 'collection', source, costsLink: { name: 'costs', query: { departmentId: 'd1' } } })
    await flushPromises()

    expect(wrapper.get('[data-test=cost-indexing]').text()).toBe(formatUsdCost(0.02))
    expect(wrapper.get('[data-test=cost-evaluation]').text()).toBe(formatUsdCost(0.004))
    expect(wrapper.find('[data-test=cost-clients]').exists()).toBe(false)
    expect(wrapper.text()).toContain('Indexing')
    expect(wrapper.text()).toContain('Answer judging')
    expect(wrapper.getComponent(RouterLinkStub).props('to')).toEqual({ name: 'costs', query: { departmentId: 'd1' } })
  })

  it('shows the gateway\'s figures for the key beside the stored ones by step, with the shared-key note when other objects share the key (B-46, #642)', async () => {
    const source = vi.fn().mockResolvedValue(costs([row({ costUsd: 0.002 }), row({ step: 'query-embed', costUsd: 0.0001 })]))
    const usage = vi.spyOn(api.gateway, 'keyUsage').mockResolvedValue({
      from: '2026-10-14', to: '2026-11-12', granularity: 'day', rows: [
        { period: '2026-11-10', step: 'chat-answer', requests: 2, successfulRequests: 2, inputTokens: 200, cachedInputTokens: 0, outputTokens: 40, costUsd: 0.003 },
        { period: '2026-11-11', step: 'chat-answer', requests: 1, successfulRequests: 1, inputTokens: 100, cachedInputTokens: 0, outputTokens: 20, costUsd: 0.001 },
      ],
    })
    const keys = vi.spyOn(api.gateway, 'keys').mockResolvedValue({ adminUrl: null, keys: [gatewayKey('k1', 2)] })
    const wrapper = mountCard({ kind: 'assistant', source, gatewayKeyId: 'k1', departmentId: 'd1' })
    await flushPromises()

    expect(usage).toHaveBeenCalledWith('k1', expect.objectContaining({ granularity: 'day' }))
    expect(keys).toHaveBeenCalledWith('d1')
    const answerRow = wrapper.get('[data-test=cost-steps]').findAll('tr').find((r) => r.text().includes('Answer'))!
    expect(answerRow.text()).toContain(formatUsdCost(0.002)) // ours
    expect(answerRow.text()).toContain(formatUsdCost(0.004)) // the gateway's, summed over the period
    expect(wrapper.get('[data-test=cost-gateway]').text()).toContain(COST_MESSAGES.gatewayFigures(formatUsdCost(0.004), '3'))
    expect(wrapper.get('[data-test=cost-shared-key]').text()).toBe(COST_MESSAGES.sharedKey)
    expect(wrapper.find('[data-test=cost-gateway-link]').exists()).toBe(false)
  })

  it('leaves the shared-key note out when only this object holds the key (#642)', async () => {
    vi.spyOn(api.gateway, 'keyUsage').mockResolvedValue({
      from: '2026-10-14', to: '2026-11-12', granularity: 'day', rows: [
        { period: '2026-11-10', step: 'chat-answer', requests: 1, successfulRequests: 1, inputTokens: 100, cachedInputTokens: 0, outputTokens: 20, costUsd: 0.001 },
      ],
    })
    const keys = vi.spyOn(api.gateway, 'keys').mockResolvedValue({ adminUrl: null, keys: [gatewayKey('k1', 1), gatewayKey('k2', 3)] })
    const wrapper = mountCard({ kind: 'assistant', source: vi.fn().mockResolvedValue(costs([row({ costUsd: 0.001 })])), gatewayKeyId: 'k1', departmentId: 'd1' })
    await flushPromises()

    expect(wrapper.find('[data-test=cost-gateway]').exists()).toBe(true)
    expect(wrapper.find('[data-test=cost-shared-key]').exists()).toBe(false)

    // The keys are read once, not again for a new period.
    await wrapper.findAll('button').find((b) => b.text() === COSTS.granularities.month)!.trigger('click')
    await flushPromises()
    expect(keys).toHaveBeenCalledTimes(1)
  })

  it('links the gateway\'s usage report when there is no read-back (B-46)', async () => {
    vi.spyOn(api.gateway, 'keyUsage').mockResolvedValue(null)
    vi.spyOn(api.gateway, 'keys').mockResolvedValue({ adminUrl: 'https://gateway-admin.example.invalid/', keys: [] })
    const wrapper = mountCard({ kind: 'assistant', source: vi.fn().mockResolvedValue(costs([row({ costUsd: 0.002 })])), gatewayKeyId: 'k1', departmentId: 'd1' })
    await flushPromises()

    expect(wrapper.find('[data-test=cost-gateway]').exists()).toBe(false)
    expect(wrapper.get('[data-test=cost-gateway-link]').attributes('href')).toBe('https://gateway-admin.example.invalid/usage')
  })

  it('says so when there were no model calls, and shows no link without one', async () => {
    const wrapper = mountCard({ kind: 'collection', source: vi.fn().mockResolvedValue(costs([])) })
    await flushPromises()
    expect(wrapper.get('[data-test=cost-empty]').text()).toBe(COST_MESSAGES.empty)
    expect(wrapper.find('[data-test=cost-total]').exists()).toBe(false)
    expect(wrapper.findComponent(RouterLinkStub).exists()).toBe(false)
  })
})

import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { api } from '@/api'
import type { KeyBudget } from '@/api/types'
import { formatUsdCost } from '@/utils/format'
import { GATEWAY_KEY } from '@/utils/labels'
import KeyBudgetStatus from './KeyBudgetStatus.vue'

function budget(extra: Partial<KeyBudget>): KeyBudget {
  return {
    scope: 'key', unit: 'usd', limit: 50, spent: 12.5, percentUsed: 25, exhausted: false, residency: null, period: 'Month',
    periodStart: '2026-11-01T00:00:00Z', periodEnd: '2026-12-01T00:00:00Z', ...extra,
  }
}

afterEach(() => vi.restoreAllMocks())

describe('The key header\'s budget status (admin design §5.5.4; B-46)', () => {
  it('shows each budget\'s scope, spent of limit with the percentage and Used up when exhausted', async () => {
    const get = vi.spyOn(api.gateway, 'keyBudget').mockResolvedValue([
      budget({}), budget({ scope: 'department', limit: 1000, spent: 1000, percentUsed: 100, exhausted: true }),
    ])
    const wrapper = mount(KeyBudgetStatus, { props: { gatewayKeyId: 'k1' } })
    await flushPromises()

    expect(get).toHaveBeenCalledWith('k1')
    const own = wrapper.get('[data-test=key-budget-key]').text()
    expect(own).toContain(GATEWAY_KEY.budgetScopes.key)
    expect(own).toContain(`${formatUsdCost(12.5)} of ${formatUsdCost(50)}`)
    expect(own).not.toContain(GATEWAY_KEY.usedUp)
    const department = wrapper.get('[data-test=key-budget-department]')
    expect(department.text()).toContain(GATEWAY_KEY.budgetScopes.department)
    expect(department.find('[data-test=budget-used-up]').exists()).toBe(true)
  })

  it('shows nothing without a key or without the read-back', async () => {
    const get = vi.spyOn(api.gateway, 'keyBudget').mockResolvedValue(null)
    expect(mount(KeyBudgetStatus, { props: { gatewayKeyId: null } }).find('[data-test=key-budgets]').exists()).toBe(false)
    expect(get).not.toHaveBeenCalled()
    const unset = mount(KeyBudgetStatus, { props: { gatewayKeyId: 'k1' } })
    await flushPromises()
    expect(unset.find('[data-test=key-budgets]').exists()).toBe(false)
  })
})

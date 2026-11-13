import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { api } from '@/api'
import type { GatewayKey } from '@/api/types'
import { GATEWAY_KEY } from '@/utils/labels'
import { KEY_UNAVAILABLE_MESSAGES } from '@/utils/messages'
import KeyUnavailableBadge from './KeyUnavailableBadge.vue'

function key(id: string, extra: Partial<GatewayKey> = {}): GatewayKey {
  return {
    id, name: 'Bygglov – chat', teamId: 't1', teamName: 'Kontaktcenter', departmentId: 'd1', status: 'Active', ownerActive: true, expiresAt: null,
    allowedModels: [], allowedResidencies: [], piiPolicy: 'Off', contentGuardPolicy: 'Off', requestsPerMinute: null, tokensPerMinute: null,
    maxConcurrentRequests: null, maxOutputTokens: null, hasSystemPrompt: false, usable: true, selectable: true, reason: null, syncedAt: '2026-11-12T10:00:00Z',
    ...extra,
  }
}

async function badge(gatewayKeyId: string | null, keys: GatewayKey[]) {
  const list = vi.spyOn(api.gateway, 'keys').mockResolvedValue({ adminUrl: null, keys })
  const wrapper = mount(KeyUnavailableBadge, { props: { gatewayKeyId, departmentId: 'd1' } })
  await flushPromises()
  return { wrapper, list }
}

afterEach(() => vi.restoreAllMocks())

describe('[Unavailable] badge (admin design §5.5.2, §5.5.4; B-44)', () => {
  it('shows "The gateway key is disabled" for a disabled key', async () => {
    const { wrapper, list } = await badge('k1', [key('k1', { status: 'Disabled', usable: true })])
    expect(list).toHaveBeenCalledWith('d1')
    expect(wrapper.get('[data-test=key-unavailable]').text()).toContain(GATEWAY_KEY.unavailable)
    expect(wrapper.get('[data-test=key-unavailable-reason]').text()).toBe('The gateway key is disabled.')
  })

  it.each([
    [{ status: 'Expired' }, 'expired'],
    [{ status: 'Revoked' }, 'revoked'],
    [{ status: 'InGracePeriod' }, 'not_active'],
    [{ ownerActive: false }, 'owner_inactive'],
    [{ usable: false }, 'unusable'],
  ] as [Partial<GatewayKey>, string][])('names the reason for %o', async (extra, reason) => {
    const { wrapper } = await badge('k1', [key('k1', extra)])
    expect(wrapper.get('[data-test=key-unavailable-reason]').text()).toBe(KEY_UNAVAILABLE_MESSAGES[reason])
  })

  it('names a spent budget from the gateway (B-46)', async () => {
    vi.spyOn(api.gateway, 'keyBudget').mockResolvedValue([
      { scope: 'department', unit: 'usd', limit: 1000, spent: 1000, percentUsed: 100, exhausted: true, residency: null, period: 'Month',
        periodStart: '2026-11-01T00:00:00Z', periodEnd: '2026-12-01T00:00:00Z' },
    ])
    const { wrapper } = await badge('k1', [key('k1')])
    expect(wrapper.get('[data-test=key-unavailable-reason]').text()).toBe(KEY_UNAVAILABLE_MESSAGES.budget_department)
  })

  it('says so when the key is no longer in the mirror', async () => {
    const { wrapper } = await badge('gone', [key('k1')])
    expect(wrapper.get('[data-test=key-unavailable-reason]').text()).toBe(KEY_UNAVAILABLE_MESSAGES.missing)
  })

  it('shows nothing for a usable key, without a key, or when the list cannot be read', async () => {
    expect((await badge('k1', [key('k1')])).wrapper.find('[data-test=key-unavailable]').exists()).toBe(false)
    vi.restoreAllMocks()
    const none = await badge(null, [])
    expect(none.wrapper.find('[data-test=key-unavailable]').exists()).toBe(false)
    expect(none.list).not.toHaveBeenCalled()
    vi.restoreAllMocks()
    vi.spyOn(api.gateway, 'keys').mockRejectedValue(new Error('offline'))
    const failed = mount(KeyUnavailableBadge, { props: { gatewayKeyId: 'k1', departmentId: 'd1' } })
    await flushPromises()
    expect(failed.find('[data-test=key-unavailable]').exists()).toBe(false)
  })
})

import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { api } from '@/api'
import type { GatewayKey } from '@/api/types'
import { GATEWAY_KEY } from '@/utils/labels'
import GatewayKeyPicker from './GatewayKeyPicker.vue'

function key(id: string, name: string, extra: Partial<GatewayKey> = {}): GatewayKey {
  return {
    id, name, teamId: 't1', teamName: 'Kontaktcenter', departmentId: 'd1', status: 'Active', ownerActive: true, expiresAt: null,
    allowedModels: ['ume/chat-eu', 'bge-m3'], allowedResidencies: ['Eu'], piiPolicy: 'Mask', contentGuardPolicy: 'Block', requestsPerMinute: 60,
    tokensPerMinute: null, maxConcurrentRequests: null, maxOutputTokens: 4096, hasSystemPrompt: false, usable: true, selectable: true, reason: null,
    syncedAt: '2026-11-12T10:00:00Z', ...extra,
  }
}

const keys = [
  key('k1', 'Chatbot'),
  key('k2', 'Paused', { status: 'Disabled', selectable: false, reason: 'not active' }),
  key('k3', 'External only', { usable: false, selectable: false, reason: 'no residency in common with the platform’s key' }),
  key('k4', 'With prompt', { hasSystemPrompt: true, allowedModels: [] }),
]

async function picker(value: string | null) {
  const list = vi.spyOn(api.gateway, 'keys').mockResolvedValue({ adminUrl: 'https://llm.example.se/', keys })
  const wrapper = mount(GatewayKeyPicker, { props: { id: 'key', departmentId: 'd1', modelValue: value, hint: 'hint' } })
  await flushPromises()
  return { wrapper, list }
}

describe('Gateway key picker (admin design §5.5.4, B-36)', () => {
  it('lists the department’s keys, an unusable one marked with its reason and not selectable', async () => {
    const { wrapper, list } = await picker(null)
    expect(list).toHaveBeenCalledWith('d1')
    const options = wrapper.findAll('option')
    expect(options.map((o) => o.text())).toEqual([
      GATEWAY_KEY.none, 'Chatbot', `Paused — ${GATEWAY_KEY.notSelectable('not active')}`,
      `External only — ${GATEWAY_KEY.notSelectable('no residency in common with the platform’s key')}`, 'With prompt',
    ])
    expect(options.map((o) => o.attributes('disabled') !== undefined)).toEqual([false, false, true, true, false])
    expect(wrapper.find('[data-test="no-key-warning"]').text()).toBe(GATEWAY_KEY.noneWarning)
    expect(wrapper.find('[data-test="key-settings"]').exists()).toBe(false)
  })

  it('shows a chosen key’s settings read-only, with a link to the gateway and no input', async () => {
    const { wrapper } = await picker('k1')
    const settings = wrapper.find('[data-test="key-settings"]')
    expect(settings.text()).toContain('Kontaktcenter')
    expect(settings.find('[data-test="key-models"]').text()).toBe('ume/chat-eu, bge-m3')
    expect(settings.text()).toContain('Mask')
    expect(settings.text()).toMatch(/4[\s\u00a0\u202f,]?096 tokens/)
    expect(settings.findAll('input, select, textarea')).toHaveLength(0)
    expect(wrapper.find('[data-test="key-link"]').attributes('href')).toBe('https://llm.example.se/keys')
    expect(wrapper.find('[data-test="no-key-warning"]').exists()).toBe(false)
  })

  it('warns about a key with a system prompt and says “All” for an empty model list', async () => {
    const { wrapper } = await picker('k4')
    expect(wrapper.find('[data-test="key-system-prompt"]').text()).toBe(GATEWAY_KEY.systemPrompt)
    expect(wrapper.find('[data-test="key-models"]').text()).toBe(GATEWAY_KEY.all)
  })

  it('keeps a saved key that is no longer usable visible, and marks one the gateway no longer lists', async () => {
    const saved = await picker('k2')
    expect(saved.wrapper.find('[data-test="key-unusable"]').exists()).toBe(true)
    expect(saved.wrapper.findAll('option').find((o) => o.attributes('value') === 'k2')?.attributes('disabled')).toBeUndefined()
    const gone = await picker('revoked')
    expect(gone.wrapper.find('[data-test="key-gone"]').text()).toBe(GATEWAY_KEY.gone)
  })

  it('emits null for “no key” and the id for a key', async () => {
    const { wrapper } = await picker(null)
    await wrapper.find('select').setValue('k1')
    await wrapper.find('select').setValue('')
    expect(wrapper.emitted('update:modelValue')).toEqual([['k1'], [null]])
  })
})

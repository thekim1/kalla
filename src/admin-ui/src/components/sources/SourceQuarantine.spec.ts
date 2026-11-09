import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { api } from '@/api'
import SourceQuarantine from './SourceQuarantine.vue'

describe('Quarantine (connectors §11)', () => {
  it('shows quarantined files as metadata only', async () => {
    vi.spyOn(api.sources, 'quarantine').mockResolvedValue({
      items: [{ id: 'q1', origin: 'upload', name: 'eicar.pdf', url: null, signature: 'Win.Test.EICAR_HDB-1', runId: null, detectedAt: '2026-11-01T10:00:00Z', purgeAfter: '2026-12-01T10:00:00Z' }],
      next: null,
    })
    const wrapper = mount(SourceQuarantine, { props: { sourceId: 's1' } })
    await flushPromises()
    expect(wrapper.text()).toContain('eicar.pdf')
    expect(wrapper.text()).toContain('Win.Test.EICAR_HDB-1')
    expect(wrapper.find('a').exists()).toBe(false) // never a download
  })
})

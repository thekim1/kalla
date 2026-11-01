import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import GenericForm from './GenericForm.vue'
import type { FieldSpec } from './schema'

const fields: FieldSpec[] = [
  { name: 'name', label: 'name', kind: 'text', required: true, nullable: false },
  { name: 'allowedOrigins', label: 'allowedOrigins', kind: 'list', required: false, nullable: false },
  { name: 'value', label: 'value', kind: 'json', required: false, nullable: false },
]

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

afterEach(() => {
  vi.unstubAllGlobals()
  document.cookie = 'RAG-XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT'
})

describe('GenericForm', () => {
  it('sends the edited values with the version it read', async () => {
    document.cookie = 'RAG-XSRF-TOKEN=t'
    const fetch = vi.fn().mockResolvedValue(json({ id: 'c1' }))
    vi.stubGlobal('fetch', fetch)
    const wrapper = mount(GenericForm, {
      props: { formId: 'f', fields, initial: { name: 'Open WebUI', allowedOrigins: ['https://a'], value: { x: 1 }, version: '7' }, method: 'put', url: '/api/clients/c1', submitLabel: 'Spara' },
    })
    await wrapper.get('#f-allowedOrigins').setValue('https://a\nhttps://b\n')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    const [url, init] = fetch.mock.calls[0]!
    expect(url).toBe('/api/clients/c1')
    expect(init.method).toBe('PUT')
    expect(JSON.parse(init.body)).toEqual({ name: 'Open WebUI', allowedOrigins: ['https://a', 'https://b'], value: { x: 1 }, version: '7' })
    expect(wrapper.emitted('saved')?.[0]).toEqual([{ id: 'c1' }])
  })

  it('stops at bad JSON and a missing required field, and shows the server field errors', async () => {
    document.cookie = 'RAG-XSRF-TOKEN=t'
    const fetch = vi.fn().mockResolvedValue(json({ status: 400, errors: { name: ['Namnet finns redan.'] } }, 400))
    vi.stubGlobal('fetch', fetch)
    const wrapper = mount(GenericForm, { props: { formId: 'f', fields, initial: {}, method: 'post', url: '/api/clients', submitLabel: 'Skapa' }, attachTo: document.body })
    await wrapper.get('#f-value').setValue('{nope')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.text()).toContain('Det här är inte giltig JSON.')
    expect(wrapper.text()).toContain('Fyll i fältet.')
    expect(fetch).not.toHaveBeenCalled()

    await wrapper.get('#f-value').setValue('')
    await wrapper.get('#f-name').setValue('Open WebUI')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(fetch).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('Namnet finns redan.')
    wrapper.unmount()
  })
})

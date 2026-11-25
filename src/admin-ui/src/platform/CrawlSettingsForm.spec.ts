import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { PLATFORM } from '@/utils/labels'
import CrawlSettingsForm from './CrawlSettingsForm.vue'
import { differences } from './politeness'

const L = PLATFORM.politeness

const initial = {
  version: '12',
  value: {
    allowedPorts: [8443],
    municipalHosts: ['*.umea.se'],
    hostOverrides: {
      'www.umea.se': {
        preset: 'fast',
        requestsPerSecond: 20,
        crawlWindow: 'any',
        circuitBreaker: 'off',
        robots: { mode: 'ignore', reason: 'own site, web team consent 2026-11-10' },
      },
      'bibliotek.example.org': { maxConnections: 2, userAgent: 'Mozilla/5.0' },
    },
  },
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

function input(selector: string, value: string): void {
  const el = document.body.querySelector<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(selector)!
  el.value = value
  el.dispatchEvent(new Event(el instanceof HTMLSelectElement ? 'change' : 'input'))
}

const click = (test: string) => document.body.querySelector<HTMLButtonElement>(`[data-test=${test}]`)!.click()

function rows(): string[][] {
  return [...document.body.querySelectorAll('[data-test=host-politeness] tbody tr')].map((tr) =>
    // A cell's value, without the column label the table repeats for narrow screens.
    [...tr.querySelectorAll('th, td')].map((cell) => (cell.querySelector('.min-w-0') ?? cell).textContent!.replace(/\s+/g, ' ').trim()),
  )
}

function mountForm() {
  return mount(CrawlSettingsForm, { props: { initial, url: '/api/settings/crawl' }, attachTo: document.body })
}

afterEach(() => {
  vi.unstubAllGlobals()
  document.cookie = 'RAG-XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT'
})

describe('Crawl settings: host politeness (admin design §5.8, connectors design §6.6)', () => {
  it('lists each host with its preset, the settings that differ from the preset and the robots.txt reason', () => {
    const wrapper = mountForm()

    const [bibliotek, umea] = rows()
    expect(bibliotek![0]).toContain('bibliotek.example.org')
    expect(bibliotek![1]).toBe(L.presets['']!)
    expect(bibliotek![2]).toBe(`${L.differ.connections(2)} · ${L.differ.userAgent('Mozilla/5.0')}`)
    expect(bibliotek![3]).toBe('–')
    expect(umea![1]).toBe(L.presets.fast!)
    // crawlWindow any is what fast sets anyway: not a difference.
    expect(umea![2]).toBe(`${L.differ.rate(20)} · ${L.differ.breakerOff}`)
    expect(umea![3]).toBe('own site, web team consent 2026-11-10')
    // The JSON keeps the other members only.
    expect(JSON.parse(document.body.querySelector<HTMLTextAreaElement>('#platform-crawl-other')!.value)).toEqual({ allowedPorts: [8443], municipalHosts: ['*.umea.se'] })
    wrapper.unmount()
  })

  it('adds a host in its drawer, which asks for a reason before robots.txt is ignored, and saves the section with the version', async () => {
    document.cookie = 'RAG-XSRF-TOKEN=t'
    const fetch = vi.fn().mockResolvedValue(json({ value: {}, version: '13' }))
    vi.stubGlobal('fetch', fetch)
    const wrapper = mountForm()

    click('add-host')
    await flushPromises()
    input('#politeness-host', 'intranat.umea.se')
    input('#politeness-preset', 'municipal')
    input('#politeness-requestsPerSecond', '2,5')
    input('#politeness-crawlDelay', 'ignore')
    input('#politeness-robots', 'ignore')
    await flushPromises()
    click('apply-host')
    await flushPromises()
    expect(document.body.textContent).toContain(L.reasonRequired)
    expect(rows()).toHaveLength(2)

    input('#politeness-reason', 'own intranet, web team consent')
    input('#politeness-userAgent', 'Mozilla/5.0 (X11; Linux x86_64)')
    click('apply-host')
    await flushPromises()
    expect(rows().map((r) => r[0])).toEqual(['bibliotek.example.org', 'intranat.umea.se', 'www.umea.se'])

    click('save-bar-save')
    await flushPromises()
    const [url, init] = fetch.mock.calls[0]!
    expect(url).toBe('/api/settings/crawl')
    expect(init.method).toBe('PUT')
    const body = JSON.parse(init.body)
    expect(body.version).toBe('12')
    expect(body.value.allowedPorts).toEqual([8443])
    expect(body.value.hostOverrides['intranat.umea.se']).toEqual({
      preset: 'municipal',
      requestsPerSecond: 2.5,
      crawlDelay: 'ignore',
      robots: { mode: 'ignore', reason: 'own intranet, web team consent' },
      userAgent: 'Mozilla/5.0 (X11; Linux x86_64)',
    })
    expect(body.value.hostOverrides['www.umea.se']).toEqual(initial.value.hostOverrides['www.umea.se'])
    expect(wrapper.emitted('saved')).toHaveLength(1)
    wrapper.unmount()
  })

  it('edits and removes a host, and refuses a host name with a port', async () => {
    const wrapper = mountForm()

    document.body.querySelectorAll<HTMLElement>('[data-test=host-politeness] tbody tr')[0]!.click()
    await flushPromises()
    expect(document.body.querySelector<HTMLInputElement>('#politeness-maxConnections')!.value).toBe('2')
    expect(document.body.querySelector<HTMLInputElement>('#politeness-host')!.disabled).toBe(true)
    input('#politeness-maxConnections', '3')
    input('#politeness-breaker', 'custom')
    await flushPromises()
    input('#politeness-threshold', '0.8')
    click('apply-host')
    await flushPromises()
    expect(rows()[0]![2]).toBe(`${L.differ.connections(3)} · ${L.differ.breaker(0.8, undefined)} · ${L.differ.userAgent('Mozilla/5.0')}`)

    document.body.querySelectorAll<HTMLElement>('[data-test=host-politeness] tbody tr')[1]!.click()
    await flushPromises()
    click('remove-host')
    await flushPromises()
    expect(rows().map((r) => r[0])).toEqual(['bibliotek.example.org'])

    click('add-host')
    await flushPromises()
    input('#politeness-host', 'www.umea.se:8443')
    click('apply-host')
    await flushPromises()
    expect(document.body.textContent).toContain(L.hostInvalid)
    wrapper.unmount()
  })

  it('shows the server’s V37 refusal in English', async () => {
    document.cookie = 'RAG-XSRF-TOKEN=t'
    const problem = {
      status: 400,
      code: 'crawl_politeness_invalid',
      params: { host: 'www.umea.se', field: 'requestsPerSecond', problem: 'must be between 0.1 and 50 (the ceiling is Crawl:MaxHostRate)' },
    }
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json(problem, 400)))
    const wrapper = mountForm()
    // The save bar shows once something changed (admin §5.12 rule 7).
    expect(document.body.querySelector('[data-test=save-bar]')).toBeNull()
    await wrapper.get('#platform-crawl-other').setValue('{ "maxPages": 100 }')
    expect(document.body.querySelector('[data-test=save-bar-count]')!.textContent).toContain('1')

    click('save-bar-save')
    await flushPromises()

    expect(document.body.textContent).toContain(
      'The politeness setting requestsPerSecond for www.umea.se is not valid: must be between 0.1 and 50 (the ceiling is Crawl:MaxHostRate)',
    )
    expect(wrapper.emitted('saved')).toBeUndefined()
    wrapper.unmount()
  })

  it('names only the members that differ from an explicit preset', () => {
    expect(differences({ preset: 'fast', requestsPerSecond: 10, crawlDelay: 'ignore', crawlWindow: 'source' })).toEqual([L.differ.sourceWindow])
    expect(differences({ preset: 'custom', requestsPerSecond: 1 })).toEqual([L.differ.rate(1)])
  })
})

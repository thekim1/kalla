import { describe, expect, it } from 'vitest'
import { normaliseTag, ruleInWords, tagError } from './topics'

describe('Topics in words and the tag check (admin design §5.5.2, processing §7)', () => {
  it('words every rule kind', () => {
    expect(ruleInWords({ on: 'path', match: '/bygga/' })).toBe('under /bygga/')
    expect(ruleInWords({ on: 'path', match: 'www.umea.se/kommun/**' })).toBe('under www.umea.se/kommun/')
    expect(ruleInWords({ on: 'path', match: '/protokoll/*.pdf' })).toBe('whose path matches /protokoll/*.pdf')
    expect(ruleInWords({ on: 'title-regex', match: 'Protokoll\\ 2024' })).toBe('whose title contains “Protokoll 2024”')
    expect(ruleInWords({ on: 'title-regex', match: '^Protokoll \\d{4}' })).toBe('whose title matches ^Protokoll \\d{4}')
    expect(ruleInWords({ on: 'mediaType', match: 'application/pdf' })).toBe('PDF files')
    expect(ruleInWords({ on: 'mediaType', match: 'image/png' })).toBe('image/png files')
    expect(ruleInWords({ on: 'host', match: 'www.umea.se' })).toBe('on www.umea.se')
    expect(ruleInWords({ on: 'path-regex', match: '^/a/(?<y>\\d{4})/' })).toBe('whose path matches the expression ^/a/(?<y>\\d{4})/')
  })

  it('checks a tag as typed, as the worker normalises it', () => {
    expect(tagError('bygglov')).toBeNull()
    expect(tagError(' Äldre-omsorg ')).toBeNull() // lower-cased and trimmed, as chunks carry it
    expect(normaliseTag(' Äldre-omsorg ')).toBe('äldre-omsorg')
    expect(tagError('')).toBe('Write a topic.')
    expect(tagError('bygga bo')).toBe('Use lower-case letters, digits and hyphens, at most 50.')
    expect(tagError('a'.repeat(51))).not.toBeNull()
  })
})

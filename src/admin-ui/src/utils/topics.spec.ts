import { describe, expect, it } from 'vitest'
import type { PathRuleJson } from '@/api/types'
import { addRules, escapeRegex, findRule, normaliseTag, plainText, removeTopic, replaceRule, ruleForm, ruleInWords, tagError } from './topics'

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

  it('escapes a title as a literal expression and reads it back', () => {
    const pattern = escapeRegex('Protokoll (2024) 1.0 [utkast]')
    expect(pattern).toBe('Protokoll \\(2024\\) 1\\.0 \\[utkast\\]')
    expect(plainText(pattern)).toBe('Protokoll (2024) 1.0 [utkast]')
    expect(new RegExp(pattern).test('Protokoll (2024) 1.0 [utkast] från mötet')).toBe(true)
  })

  it('shows a rule in words when it can, under Advanced when it needs more, and as JSON otherwise', () => {
    const rule = (r: Partial<PathRuleJson>): PathRuleJson => ({ match: '/a/', on: 'path', tags: ['a'], ...r })
    expect(ruleForm(rule({}))).toBe('under')
    expect(ruleForm(rule({ on: 'title-regex', match: 'Protokoll' }))).toBe('title')
    expect(ruleForm(rule({ on: 'title-regex', match: '^Protokoll' }))).toBe('advanced')
    expect(ruleForm(rule({ on: 'mediaType', match: 'application/pdf' }))).toBe('type')
    expect(ruleForm(rule({ on: 'mediaType', match: 'image/png' }))).toBe('advanced')
    expect(ruleForm(rule({ on: 'host', match: 'www.umea.se' }))).toBe('advanced')
    expect(ruleForm(rule({ on: 'path-regex', match: '^/p/(?<y>\\d{4})/', date: '${y}-01-01' }))).toBe('advanced')
    expect(ruleForm(rule({ documentType: 'page' }))).toBe('advanced')
    expect(ruleForm(rule({ tags: ['a', 'b'] }))).toBe('json')
    expect(ruleForm({ ...rule({}), extra: true } as PathRuleJson)).toBe('json')
  })

  it('replaces only the rule of the topic, keeps the other topics of a shared rule, and removes a topic everywhere', () => {
    const rules: PathRuleJson[] = [
      { match: '/bygga/', on: 'path', tags: ['bygga'] },
      { match: '/kommun/', on: 'path', tags: ['kommun', 'politik'] },
      { match: 'Protokoll', on: 'title-regex', tags: ['Politik'] },
    ]
    expect(findRule(rules, 'politik', 'title-regex', 'Protokoll')).toBe(2) // the stored tag is compared as chunks carry it
    const next: PathRuleJson = { match: '/bygga-bo/', on: 'path', tags: ['bygga'] }
    expect(replaceRule('bygga', 'path', '/bygga/', next)(rules)).toEqual([next, rules[1], rules[2]])
    expect(replaceRule('politik', 'path', '/kommun/', { match: '/politik/', on: 'path', tags: ['politik'] })(rules)).toEqual([
      rules[0], { match: '/kommun/', on: 'path', tags: ['kommun'] }, rules[2], { match: '/politik/', on: 'path', tags: ['politik'] },
    ])
    expect(removeTopic('politik')(rules)).toEqual([rules[0], { match: '/kommun/', on: 'path', tags: ['kommun'] }])
    expect(addRules([next])(rules)).toEqual([...rules, next])
  })
})

describe('The post-merge fixes of the topic rules (#1075, #1076)', () => {
  it('words a path rule as the matcher reads it: one address, a pasted address without its query', () => {
    expect(ruleInWords({ on: 'path', match: '/kontakt' })).toBe('at /kontakt')
    expect(ruleInWords({ on: 'path', match: 'HR/Policys/' })).toBe('under HR/Policys/')
    expect(ruleInWords({ on: 'path', match: 'https://www.umea.se/bygga/?utm_source=x#top' })).toBe('under www.umea.se/bygga/')
    expect(ruleInWords({ on: 'path', match: 'https://www.umea.se/kontakt?x=1' })).toBe('at www.umea.se/kontakt')
    expect(ruleInWords({ on: 'title-regex', match: '(?i)Protokoll' })).toBe('whose title contains “Protokoll”')
  })

  it('adds a rule that matches as an existing one does as topics on that rule, never twice', () => {
    const rules: PathRuleJson[] = [{ match: '/omsorg/', on: 'path', tags: ['omsorg'] }]
    const same = { match: '/omsorg/', on: 'path' as const, tags: ['omsorg'] }
    expect(addRules([same, same])(rules)).toEqual(rules)
    expect(addRules([{ ...same, tags: ['vard'] }])(rules)).toEqual([{ match: '/omsorg/', on: 'path', tags: ['omsorg', 'vard'] }])
    // A document type makes it another rule.
    expect(addRules([{ ...same, documentType: 'page' }])(rules)).toHaveLength(2)
  })

  it('replaces a rule edited as its JSON as a whole, and only this topic\'s otherwise', () => {
    const rules: PathRuleJson[] = [{ match: '/bygga/', on: 'path', tags: ['bygg', 'lov'] }]
    expect(replaceRule('bygg', 'path', '/bygga/', rules[0]!, true)(rules)).toEqual(rules)
    const narrowed = { match: '/bygga/lov/', on: 'path' as const, tags: ['bygg', 'lov'] }
    expect(replaceRule('bygg', 'path', '/bygga/', narrowed, true)(rules)).toEqual([narrowed])
    expect(replaceRule('bygg', 'path', '/bygga/', { match: '/bygga/', on: 'path', tags: ['bygg'] })(rules))
      .toEqual([{ match: '/bygga/', on: 'path', tags: ['lov', 'bygg'] }])
  })

  it('reads a rule without topics (only a document type) without failing', () => {
    const rules: PathRuleJson[] = [{ match: '/blankett/', on: 'path', documentType: 'form' }, { match: '/bygga/', on: 'path', tags: ['bygga'] }]
    expect(removeTopic('bygga')(rules)).toEqual([rules[0]])
    expect(findRule(rules, 'bygga', 'path', '/blankett/')).toBe(-1)
    expect(ruleForm(rules[0]!)).toBe('json')
  })
})

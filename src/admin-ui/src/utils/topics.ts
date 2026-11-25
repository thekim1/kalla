import type { PathRuleJson, Topic } from '@/api/types'
import { TOPICS } from '@/utils/labels'

/** The chunk tag format (processing design §7): lower case, NFC, letters, digits and `-`, 1–50 characters. */
const TAG = /^[\p{L}\p{N}-]{1,50}$/u

/** A tag as chunks carry it (trimmed, lower case, NFC), as the worker normalises it. */
export function normaliseTag(tag: string): string {
  return tag.trim().toLowerCase().normalize('NFC')
}

/** The tag check as typed on the Topics tab: null when the tag is valid, else why not. */
export function tagError(tag: string): string | null {
  const normal = normaliseTag(tag)
  if (normal.length === 0) return TOPICS.tagRequired
  return TAG.test(normal) ? null : TOPICS.tagInvalid
}

/** The form writes "whose title contains" case-insensitive (#1076): .NET's inline option, ahead of the escaped text. */
export const IGNORE_CASE = '(?i)'

/** A rule's topics; a rule may set none (only a document type or a date). */
export const tagsOf = (rule: { tags?: string[] }): string[] => rule.tags ?? []

/**
 * A regular expression that is only escaped text (`Protokoll\ 2024`), with or without a leading `(?i)`: that text,
 * else null.
 */
export function plainText(full: string): string | null {
  const pattern = full.startsWith(IGNORE_CASE) ? full.slice(IGNORE_CASE.length) : full
  let text = ''
  for (let i = 0; i < pattern.length; i++) {
    const c = pattern[i]!
    if (c === '\\') {
      const next = pattern[i + 1]
      if (next === undefined || /[\p{L}\p{N}]/u.test(next)) return null // \d, \w, \b … are patterns, not text
      text += next
      i++
    } else if ('.^$*+?()[]{}|'.includes(c)) return null
    else text += c
  }
  return text
}

export const MEDIA_TYPES: Record<string, string> = {
  'application/pdf': 'PDF',
  'text/html': 'HTML',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document': 'Word',
  'application/msword': 'Word 97–2003',
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': 'Excel',
  'application/vnd.ms-excel': 'Excel 97–2003',
  'application/vnd.openxmlformats-officedocument.presentationml.presentation': 'PowerPoint',
  'application/vnd.ms-powerpoint': 'PowerPoint 97–2003',
  'application/vnd.oasis.opendocument.text': 'OpenDocument text',
  'application/vnd.oasis.opendocument.spreadsheet': 'OpenDocument spreadsheet',
  'application/vnd.oasis.opendocument.presentation': 'OpenDocument presentation',
  'text/csv': 'CSV',
  'text/plain': 'Text',
  'text/markdown': 'Markdown',
}

/**
 * A `path` rule's glob as the matcher reads it (processing §7; `PathRules.PathGlob`): a pasted address loses its scheme,
 * query and fragment; a host-led one keeps its host in front.
 */
function pathShown(match: string): string {
  const bare = match.replace(/^https?:\/\//i, '')
  const hostLed = bare !== match || /^[^/]+\.[\p{L}]{2,}(:\d+)?(\/|$)/u.test(bare)
  return hostLed ? bare.replace(/[?#].*$/, '') : match
}

/** A path rule in words for the topic list (admin design §5.5.2): "under /bygga/", "whose title contains …", "PDF files". */
export function ruleInWords(rule: { on: string; match: string }): string {
  switch (rule.on) {
    case 'path': {
      // A glob that ends in /** covers what is under the folder, as one that ends in / does; without either it is one
      // address (processing §7).
      const shown = pathShown(rule.match).replace(/\/\*\*$/, '/')
      if (/[*?]/.test(shown)) return TOPICS.words.pathPattern(shown)
      return shown.endsWith('/') ? TOPICS.words.under(shown) : TOPICS.words.at(shown)
    }
    case 'title-regex': {
      const text = plainText(rule.match)
      return text === null ? TOPICS.words.titleMatches(rule.match) : TOPICS.words.titleContains(text)
    }
    case 'mediaType':
      return TOPICS.words.mediaType(MEDIA_TYPES[rule.match.toLowerCase()] ?? rule.match)
    case 'host':
      return TOPICS.words.host(rule.match)
    case 'path-regex':
      return TOPICS.words.pathRegex(rule.match)
    default:
      return `${rule.on}: ${rule.match}`
  }
}

/** The sources a topic's rules are on, each once, in the rules' order. */
export function topicSources(topic: Topic): string[] {
  return [...new Set((topic.rules ?? []).map((r) => r.sourceName))]
}

/** Text as a regular expression that matches it literally (the form's "whose title contains", written as `title-regex`). */
export function escapeRegex(text: string): string {
  return text.replace(/[.^$*+?()[\]{}|\\]/g, (c) => `\\${c}`)
}

/** How the topic form shows a rule (§5.5.2): in words when it can, else under Advanced, else as its JSON. */
export type RuleForm = 'under' | 'title' | 'type' | 'advanced' | 'json'

const WORDED_KEYS = new Set(['match', 'on', 'tags', 'documentType', 'date'])

export function ruleForm(rule: PathRuleJson): RuleForm {
  if (Object.keys(rule).some((k) => !WORDED_KEYS.has(k)) || tagsOf(rule).length !== 1) return 'json'
  if (rule.documentType !== undefined || rule.date !== undefined) return 'advanced'
  if (rule.on === 'path') return 'under'
  if (rule.on === 'title-regex' && plainText(rule.match) !== null) return 'title'
  if (rule.on === 'mediaType' && MEDIA_TYPES[rule.match.toLowerCase()]) return 'type'
  return 'advanced'
}

/** The rule as stored that sets `tag` with this `on` and `match` (what `GET …/topics` names), or -1. */
export function findRule(rules: PathRuleJson[], tag: string, on: string, match: string): number {
  return rules.findIndex((r) => r.on === on && r.match === match && tagsOf(r).map(normaliseTag).includes(tag))
}

/** The same documents with the same document type and date: two such rules differ only in their topics. */
const sameMatch = (a: PathRuleJson, b: PathRuleJson) => a.on === b.on && a.match === b.match && a.documentType === b.documentType && a.date === b.date

/**
 * Adds rules (#1075): a rule that matches as an existing one does gains its topics on that rule instead of being stored
 * a second time, and a topic it already sets is not added again.
 */
export const addRules = (added: PathRuleJson[]) => (rules: PathRuleJson[]): PathRuleJson[] =>
  added.reduce((out, rule) => {
    const at = out.findIndex((r) => sameMatch(r, rule))
    if (at < 0) return [...out, rule]
    const have = out[at]!
    const known = new Set(tagsOf(have).map(normaliseTag))
    const more = tagsOf(rule).filter((t) => !known.has(normaliseTag(t)))
    return more.length ? out.map((r, i) => (i === at ? { ...have, tags: [...tagsOf(have), ...more] } : r)) : out
  }, rules)

/**
 * Replaces the rule that sets `tag` (`on` and `match` as listed) by `next`. A rule that sets other topics too keeps them
 * without this one, and `next` is added after it: only this topic's rule changes. With `whole` (the rule was edited as
 * its JSON, every topic in view) the stored rule is replaced as it is (#1076).
 */
export const replaceRule = (tag: string, on: string, match: string, next: PathRuleJson, whole = false) => (rules: PathRuleJson[]): PathRuleJson[] => {
  const at = findRule(rules, tag, on, match)
  if (at < 0) return addRules([next])(rules)
  const old = rules[at]!
  if (whole || tagsOf(old).length <= 1) return rules.map((r, i) => (i === at ? next : r))
  return addRules([next])(rules.map((r, i) => (i === at ? { ...r, tags: tagsOf(r).filter((t) => normaliseTag(t) !== tag) } : r)))
}

/** Removes a topic: each rule that sets only it goes, and a rule that sets others too keeps them. */
export const removeTopic = (tag: string) => (rules: PathRuleJson[]): PathRuleJson[] =>
  rules.flatMap((r) => {
    if (!tagsOf(r).map(normaliseTag).includes(tag)) return [r]
    const tags = tagsOf(r).filter((t) => normaliseTag(t) !== tag)
    return tags.length === 0 ? [] : [{ ...r, tags }]
  })

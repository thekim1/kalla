import type { Topic } from '@/api/types'
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

/** A regular expression that is only escaped text (`Protokoll\ 2024`): that text, else null. */
function plainText(pattern: string): string | null {
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

const MEDIA_TYPES: Record<string, string> = {
  'application/pdf': 'PDF',
  'text/html': 'HTML',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document': 'Word',
  'application/msword': 'Word',
  'application/vnd.openxmlformats-officedocument.presentationml.presentation': 'PowerPoint',
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': 'Excel',
  'text/plain': 'Text',
  'text/markdown': 'Markdown',
}

/** A path rule in words for the topic list (admin design §5.5.2): "under /bygga/", "whose title contains …", "PDF files". */
export function ruleInWords(rule: { on: string; match: string }): string {
  switch (rule.on) {
    case 'path':
      // A glob that ends in /** covers what is under the folder, as one that ends in / does (processing §7).
      return /[*?]/.test(rule.match.replace(/\/\*\*$/, '/')) ? TOPICS.words.pathPattern(rule.match) : TOPICS.words.under(rule.match.replace(/\/\*\*$/, '/'))
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

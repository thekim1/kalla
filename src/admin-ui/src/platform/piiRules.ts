import { PII_RULES } from '@/utils/labels'

/**
 * `Settings.PiiFlagRules` as the Settings page edits it (processing design §9.4; `PiiFlagRules` in
 * `Kalla.Pipelines/Processing/PersonalData.cs`). Stored as `{ types: { <type>: { flag, min } }, listLike: { minNames,
 * minContacts } | null }`; an empty object means the defaults below. A type the stored `types` leave out never flags.
 */

/** In the order the table shows them (`PiiTypes.All`). */
export const PII_TYPES = ['personnummer', 'samordningsnummer', 'emailExternal', 'email', 'phone', 'name'] as const
export type PiiType = (typeof PII_TYPES)[number]
export type PiiFlag = 'always' | 'threshold' | 'never'

/** The worker's defaults (`PiiFlagRules.Default`): the identity numbers always, an external address from 1, lists of 5 and 5. */
const DEFAULT_TYPES: Partial<Record<PiiType, { flag: PiiFlag; min: number }>> = {
  personnummer: { flag: 'always', min: 1 },
  samordningsnummer: { flag: 'always', min: 1 },
  emailExternal: { flag: 'threshold', min: 1 },
}
const DEFAULT_LIST = { minNames: 5, minContacts: 5 }
const DEFAULT_NER_MIN_SCORE = 0.8

/** The controls' values: numbers as typed, so they can be checked on save. */
export interface PiiRulesForm {
  ownDomains: string
  nerMinScore: string
  types: Record<string, { flag: PiiFlag; min: string }>
  listLike: boolean
  minNames: string
  minContacts: string
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

/** The form from `GET /api/settings/pii`: the stored rules, or the defaults when none are stored. */
export function piiRulesForm(initial: Record<string, unknown>): PiiRulesForm {
  const rules = isObject(initial.flagRules) ? initial.flagRules : {}
  const stored = Object.keys(rules).length > 0
  const types = stored ? (isObject(rules.types) ? rules.types : undefined) : undefined
  const list = stored ? (rules.listLike === undefined ? DEFAULT_LIST : rules.listLike) : DEFAULT_LIST
  const form: PiiRulesForm = {
    ownDomains: Array.isArray(initial.ownDomains) ? initial.ownDomains.join('\n') : '',
    nerMinScore: String(typeof initial.nerMinScore === 'number' ? initial.nerMinScore : DEFAULT_NER_MIN_SCORE),
    types: {},
    listLike: isObject(list),
    minNames: String(isObject(list) && typeof list.minNames === 'number' ? list.minNames : DEFAULT_LIST.minNames),
    minContacts: String(isObject(list) && typeof list.minContacts === 'number' ? list.minContacts : DEFAULT_LIST.minContacts),
  }
  for (const t of PII_TYPES) {
    // Stored `types` replace the defaults whole: a type they leave out never flags. No `types` member keeps the defaults.
    const rule = types ? types[t] : DEFAULT_TYPES[t]
    const flag = isObject(rule) && (rule.flag === 'always' || rule.flag === 'threshold' || rule.flag === 'never') ? rule.flag : 'never'
    const min = isObject(rule) && typeof rule.min === 'number' ? rule.min : 1
    form.types[t] = { flag: flag as PiiFlag, min: String(min) }
  }
  return form
}

function wholeNumber(text: string): number | null {
  const n = Number(text.trim())
  return Number.isInteger(n) && n >= 1 ? n : null
}

/** The request body (without `version`) and the fields that cannot be read. Every type is sent with its rule. */
export function piiRulesRequest(form: PiiRulesForm): { body: Record<string, unknown>; problems: Record<string, string> } {
  const problems: Record<string, string> = {}
  const types: Record<string, { flag: PiiFlag; min: number }> = {}
  for (const t of PII_TYPES) {
    const rule = form.types[t]!
    let min = 1
    if (rule.flag === 'threshold') {
      const n = wholeNumber(rule.min)
      if (n === null) problems[`min.${t}`] = PII_RULES.wholeNumber
      else min = n
    }
    types[t] = { flag: rule.flag, min }
  }
  let listLike: { minNames: number; minContacts: number } | null = null
  if (form.listLike) {
    const names = wholeNumber(form.minNames)
    const contacts = wholeNumber(form.minContacts)
    if (names === null) problems.minNames = PII_RULES.wholeNumber
    if (contacts === null) problems.minContacts = PII_RULES.wholeNumber
    listLike = { minNames: names ?? 1, minContacts: contacts ?? 1 }
  }
  const score = Number(form.nerMinScore.trim().replace(',', '.'))
  if (form.nerMinScore.trim() === '' || !Number.isFinite(score) || score < 0 || score > 1) problems.nerMinScore = PII_RULES.scoreRange
  return {
    body: {
      flagRules: { types, listLike },
      ownDomains: form.ownDomains.split('\n').map((l) => l.trim()).filter(Boolean),
      nerMinScore: score,
    },
    problems,
  }
}

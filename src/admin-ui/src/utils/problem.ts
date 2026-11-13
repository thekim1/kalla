import { isApiError } from '@/api/client'
import { ERROR_MESSAGES, STATUS_MESSAGES, formatMessage } from './messages'

/**
 * The user's message for a failed call (adapted from the gateway, admin design §4.2): the problem's `code` through
 * messages.ts, else a text for the status. The API's `detail` is never the message (it is not written for these users);
 * `englishDetail` offers it as a last resort.
 */
export function problemMessage(error: unknown): string {
  if (!isApiError(error)) return STATUS_MESSAGES.unknown
  if (error.status === 0) return STATUS_MESSAGES.offline
  const template = codeMessage(error.problem?.code)
  if (template) return formatMessage(template, error.problem?.params)
  switch (error.status) {
    case 401:
    case 403:
    case 404:
    case 409:
    case 429:
      return STATUS_MESSAGES[error.status]
    default:
      return error.status >= 500 ? STATUS_MESSAGES.server : STATUS_MESSAGES.rejected
  }
}

/** The API's English detail when no code matched, for display as `<span lang="en">`; null otherwise. */
export function englishDetail(error: unknown): string | null {
  if (!isApiError(error) || codeMessage(error.problem?.code)) return null
  return error.problem?.detail || null
}

/**
 * Maps a validation problem onto form field names (any casing), first message per field. A field's error code from
 * `errorCodes` wins over its English message (admin design §4.2); when the code's params name a `subject` (the one of several
 * objects the field lists that was refused, admin design §5.9.1), the text starts with it. Adapted from the gateway.
 */
export function mapFieldErrors(error: unknown, fields: readonly string[]): Record<string, string> {
  const result: Record<string, string> = {}
  if (!isApiError(error)) return result
  const codes = error.problem?.errorCodes ?? {}
  for (const [key, messages] of Object.entries(error.fieldErrors)) {
    // `Targets[0].weight` and `targets[1]` both belong to the `targets` field.
    const match = fields.find((f) => f.toLowerCase() === key.toLowerCase().replace(/^\$\./, '').replace(/\[\d+\].*$/, ''))
    const coded = codes[key]?.[0]
    const template = codeMessage(coded?.code)
    const text = template ? formatMessage(template, coded?.params) : messages[0]
    const subject = template ? coded?.params?.subject : undefined
    const message = subject && text ? `${subject}: ${text}` : text
    if (match && message) result[match] = message
  }
  return result
}

/**
 * Every refusal of a rule check, each with the objects it names (`affected[]`, admin design §4.6): the problem's
 * `problems[]` when the API lists them, else the one problem. Used where a save can break several rules at once (a
 * level change, #446).
 */
export function problemLines(error: unknown): string[] {
  if (!isApiError(error)) return [problemMessage(error)]
  const named = (message: string, affected: unknown) =>
    Array.isArray(affected) && affected.length ? `${message} (${affected.join(', ')})` : message
  const problems = error.problem?.problems
  if (Array.isArray(problems) && problems.length) {
    return problems.map((p: { code?: string; params?: Record<string, string>; affected?: unknown }) => {
      const template = codeMessage(p.code)
      return named(template ? formatMessage(template, p.params ?? undefined) : problemMessage(error), p.affected)
    })
  }
  return [named(problemMessage(error), error.problem?.affected)]
}

/** Seconds from a 429 Retry-After value stored in the problem extensions, if any. Copied. */
export function retryAfterSeconds(error: unknown): number | null {
  if (!isApiError(error) || error.status !== 429) return null
  const value = error.problem?.retryAfter
  return typeof value === 'number' ? value : null
}

function codeMessage(code: unknown): string | undefined {
  return typeof code === 'string' ? ERROR_MESSAGES[code] : undefined
}

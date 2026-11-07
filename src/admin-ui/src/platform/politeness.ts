import type { PolitenessReportEntry } from '@/api/types'
import { PLATFORM } from '@/utils/labels'

/**
 * A host's politeness profile in `Settings.Crawl.HostOverrides` (connectors design §6.6; the worker's `HostPoliteness`).
 * A preset fills every member; a member set here overrides it. The server checks the profile on save (V37).
 */
export type Preset = 'polite' | 'municipal' | 'fast' | 'custom'

export interface HostPoliteness {
  preset?: Preset
  requestsPerSecond?: number
  maxConnections?: number
  crawlDelay?: 'honour' | 'ignore'
  adaptToLatency?: boolean
  crawlWindow?: 'source' | 'any'
  backoff?: { maxRetryAfterSeconds?: number; maxFactor?: number }
  circuitBreaker?: 'off' | { threshold?: number; pauseMinutes?: number }
  robots?: { mode: 'respect' | 'ignore'; reason?: string }
  userAgent?: string
  renderPerHost?: number
}

export type HostOverrides = Record<string, HostPoliteness>

export const PRESETS: readonly Preset[] = ['polite', 'municipal', 'fast', 'custom']

type PresetMembers = Required<Pick<HostPoliteness, 'requestsPerSecond' | 'maxConnections' | 'crawlDelay' | 'adaptToLatency' | 'crawlWindow'>>

/** What each preset sets (the worker's `PolitenessProfile`); `custom` starts from the host kind's defaults. */
export const PRESET_MEMBERS: Readonly<Record<Exclude<Preset, 'custom'>, PresetMembers>> = {
  polite: { requestsPerSecond: 1, maxConnections: 1, crawlDelay: 'honour', adaptToLatency: true, crawlWindow: 'source' },
  municipal: { requestsPerSecond: 10, maxConnections: 4, crawlDelay: 'honour', adaptToLatency: true, crawlWindow: 'source' },
  fast: { requestsPerSecond: 10, maxConnections: 4, crawlDelay: 'ignore', adaptToLatency: false, crawlWindow: 'any' },
}

/** The members of `profile` that differ from its preset (all set members without a preset or with `custom`), as short texts. */
export function differences(profile: HostPoliteness): string[] {
  const L = PLATFORM.politeness.differ
  const basis = profile.preset && profile.preset !== 'custom' ? PRESET_MEMBERS[profile.preset] : undefined
  const differs = <K extends keyof PresetMembers>(key: K): boolean => profile[key] !== undefined && profile[key] !== basis?.[key]
  const out: string[] = []
  if (differs('requestsPerSecond')) out.push(L.rate(profile.requestsPerSecond!))
  if (differs('maxConnections')) out.push(L.connections(profile.maxConnections!))
  if (differs('crawlDelay')) out.push(profile.crawlDelay === 'ignore' ? L.crawlDelayIgnored : L.crawlDelayHonoured)
  if (differs('adaptToLatency')) out.push(profile.adaptToLatency ? L.latencyOn : L.latencyOff)
  if (differs('crawlWindow')) out.push(profile.crawlWindow === 'any' ? L.anyTime : L.sourceWindow)
  if (profile.backoff?.maxRetryAfterSeconds !== undefined) out.push(L.maxRetryAfter(profile.backoff.maxRetryAfterSeconds))
  if (profile.backoff?.maxFactor !== undefined) out.push(L.maxFactor(profile.backoff.maxFactor))
  if (profile.circuitBreaker === 'off') out.push(L.breakerOff)
  else if (profile.circuitBreaker) out.push(L.breaker(profile.circuitBreaker.threshold, profile.circuitBreaker.pauseMinutes))
  if (profile.userAgent !== undefined) out.push(L.userAgent(profile.userAgent))
  if (profile.renderPerHost !== undefined) out.push(L.renderPerHost(profile.renderPerHost))
  return out
}

/** A crawl report's host with non-default politeness: what differs from its kind's defaults, as short texts (robots.txt apart). */
export function reportDifferences(entry: PolitenessReportEntry): string[] {
  const L = PLATFORM.politeness.differ
  const out: string[] = []
  if (entry.requestsPerSecond !== null) out.push(L.rate(entry.requestsPerSecond))
  if (entry.maxConnections !== null) out.push(L.connections(entry.maxConnections))
  if (entry.crawlDelay !== null) out.push(entry.crawlDelay === 'ignore' ? L.crawlDelayIgnored : L.crawlDelayHonoured)
  if (entry.adaptToLatency !== null) out.push(entry.adaptToLatency ? L.latencyOn : L.latencyOff)
  if (entry.crawlWindow !== null) out.push(entry.crawlWindow === 'any' ? L.anyTime : L.sourceWindow)
  if (entry.maxRetryAfterSeconds !== null) out.push(L.maxRetryAfter(entry.maxRetryAfterSeconds))
  if (entry.maxBackoffFactor !== null) out.push(L.maxFactor(entry.maxBackoffFactor))
  if (entry.breakerOff) out.push(L.breakerOff)
  else if (entry.breakerThreshold !== null || entry.breakerPauseMinutes !== null) out.push(L.breaker(entry.breakerThreshold ?? undefined, entry.breakerPauseMinutes ?? undefined))
  if (entry.userAgent !== null) out.push(L.userAgent(entry.userAgent))
  if (entry.renderPerHost !== null) out.push(L.renderPerHost(entry.renderPerHost))
  return out
}

/**
 * The drawer's controls as their text: numbers as typed, `''` for "as the preset"; `preset` a {@link Preset},
 * `crawlDelay` honour/ignore, `adaptToLatency` true/false, `crawlWindow` source/any, `breaker` off/custom, `robots`
 * respect/ignore.
 */
export interface PolitenessForm {
  host: string
  preset: string
  requestsPerSecond: string
  maxConnections: string
  crawlDelay: string
  adaptToLatency: string
  crawlWindow: string
  maxRetryAfterSeconds: string
  maxFactor: string
  breaker: string
  threshold: string
  pauseMinutes: string
  robots: string
  reason: string
  userAgent: string
  renderPerHost: string
}

const text = (n: number | undefined): string => (n === undefined ? '' : String(n))

export function toForm(host: string, profile: HostPoliteness): PolitenessForm {
  const breaker = profile.circuitBreaker
  return {
    host,
    preset: profile.preset ?? '',
    requestsPerSecond: text(profile.requestsPerSecond),
    maxConnections: text(profile.maxConnections),
    crawlDelay: profile.crawlDelay ?? '',
    adaptToLatency: profile.adaptToLatency === undefined ? '' : profile.adaptToLatency ? 'true' : 'false',
    crawlWindow: profile.crawlWindow ?? '',
    maxRetryAfterSeconds: text(profile.backoff?.maxRetryAfterSeconds),
    maxFactor: text(profile.backoff?.maxFactor),
    breaker: breaker === 'off' ? 'off' : breaker ? 'custom' : '',
    threshold: typeof breaker === 'object' ? text(breaker.threshold) : '',
    pauseMinutes: typeof breaker === 'object' ? text(breaker.pauseMinutes) : '',
    robots: profile.robots?.mode === 'ignore' ? 'ignore' : 'respect',
    reason: profile.robots?.reason ?? '',
    userAgent: profile.userAgent ?? '',
    renderPerHost: text(profile.renderPerHost),
  }
}

/** The fields the drawer checks before the server does (V37 has the last word). */
export type FormProblems = Partial<Record<keyof PolitenessForm, string>>

function number(value: string): number | undefined {
  const trimmed = value.trim()
  return trimmed === '' ? undefined : Number(trimmed.replace(',', '.'))
}

/** The profile the form describes, or the problems that keep it from being one. */
export function fromForm(form: PolitenessForm): { profile: HostPoliteness; problems: FormProblems } {
  const L = PLATFORM.politeness
  const problems: FormProblems = {}
  const host = form.host.trim()
  if (!/^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$/.test(host)) problems.host = L.hostInvalid
  const numbers: Partial<Record<keyof PolitenessForm, number | undefined>> = {}
  for (const key of ['requestsPerSecond', 'maxConnections', 'maxRetryAfterSeconds', 'maxFactor', 'threshold', 'pauseMinutes', 'renderPerHost'] as const) {
    const value = number(form[key])
    if (value !== undefined && !Number.isFinite(value)) problems[key] = PLATFORM.form.invalidNumber
    numbers[key] = value
  }
  if (form.robots === 'ignore' && form.reason.trim() === '') problems.reason = L.reasonRequired

  const profile: HostPoliteness = {}
  if (form.preset) profile.preset = form.preset as Preset
  if (numbers.requestsPerSecond !== undefined) profile.requestsPerSecond = numbers.requestsPerSecond
  if (numbers.maxConnections !== undefined) profile.maxConnections = numbers.maxConnections
  if (form.crawlDelay) profile.crawlDelay = form.crawlDelay as 'honour' | 'ignore'
  if (form.adaptToLatency) profile.adaptToLatency = form.adaptToLatency === 'true'
  if (form.crawlWindow) profile.crawlWindow = form.crawlWindow as 'source' | 'any'
  if (numbers.maxRetryAfterSeconds !== undefined || numbers.maxFactor !== undefined) {
    profile.backoff = {}
    if (numbers.maxRetryAfterSeconds !== undefined) profile.backoff.maxRetryAfterSeconds = numbers.maxRetryAfterSeconds
    if (numbers.maxFactor !== undefined) profile.backoff.maxFactor = numbers.maxFactor
  }
  if (form.breaker === 'off') profile.circuitBreaker = 'off'
  else if (form.breaker === 'custom') {
    const breaker: { threshold?: number; pauseMinutes?: number } = {}
    if (numbers.threshold !== undefined) breaker.threshold = numbers.threshold
    if (numbers.pauseMinutes !== undefined) breaker.pauseMinutes = numbers.pauseMinutes
    profile.circuitBreaker = breaker
  }
  if (form.robots === 'ignore') profile.robots = { mode: 'ignore', reason: form.reason.trim() }
  if (form.userAgent.trim()) profile.userAgent = form.userAgent.trim()
  if (numbers.renderPerHost !== undefined) profile.renderPerHost = numbers.renderPerHost
  return { profile, problems }
}

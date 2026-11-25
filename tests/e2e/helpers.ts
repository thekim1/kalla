import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import AxeBuilder from '@axe-core/playwright'
import axe from 'axe-core'
import { expect, type APIRequestContext, type Locator, type Page } from '@playwright/test'

export type Role = 'rag-platform-admin' | 'rag-department-admin' | 'rag-viewer' | 'rag-no-role'

export const adminUiUrl = process.env.ADMIN_UI_URL ?? 'https://localhost:5174'
export const apiUrl = process.env.RAG_API_URL ?? 'https://localhost:7401'
/** The static Swedish website of fixture-site/ (scripts/Start-E2E.ps1 serves it on this port). */
export const fixtureSiteUrl = process.env.FIXTURE_SITE_URL ?? 'http://127.0.0.1:7416'

/** An AppHost parameter from its user secrets, or from `environment`. Never print it. */
export function appHostSecret(name: string, environment: string): string {
  const fromEnvironment = process.env[environment]
  if (fromEnvironment) return fromEnvironment
  const directory = process.env.APPDATA
    ? join(process.env.APPDATA, 'Microsoft', 'UserSecrets')
    : join(homedir(), '.microsoft', 'usersecrets')
  const secrets = JSON.parse(readFileSync(join(directory, 'kalla-apphost-dev', 'secrets.json'), 'utf8').replace(/^﻿/, ''))
  const value: unknown = secrets[`Parameters:${name}`]
  if (typeof value !== 'string' || !value) throw new Error(`Start the AppHost to generate ${name}, or set ${environment}.`)
  return value
}

/** The seeded Open WebUI (dev) client's key (DevSeeder: kalla-sk- + the AppHost's dev-key). */
export const devClientKey = () => `kalla-sk-${appHostSecret('dev-key', 'E2E_DEV_KEY')}`

/**
 * Types a secret into a field without a locator `fill`, whose value would show in the call log of a failed assertion. The
 * input event lets the page's form (Vue's v-model, Keycloak's form) take the value.
 */
export async function typeSecret(field: Locator, value: string): Promise<void> {
  await field.evaluate((element, secret) => {
    const input = element as HTMLInputElement
    input.value = secret
    input.dispatchEvent(new Event('input', { bubbles: true }))
  }, value)
}

const appHostProject = fileURLToPath(new URL('../../src/Kalla.AppHost/Kalla.AppHost.csproj', import.meta.url))

/**
 * The setup code kalla-admin wrote to its output (identity design §19) on the stack of `scripts/Start-E2E.ps1 -Setup`: from
 * `E2E_SETUP_CODE`, or the last "Setup code:" line of `aspire logs adminapi` (the CLI from `ASPIRE_CLI`, else `aspire` on the
 * path). Null while kalla-admin has not written it yet. Never print it: the output is read here and only the code is kept.
 */
export function setupCode(): string | null {
  if (process.env.E2E_SETUP_CODE) return process.env.E2E_SETUP_CODE
  const aspire = process.env.ASPIRE_CLI ?? 'aspire'
  let output: string
  try {
    output = execFileSync(aspire, ['logs', 'adminapi', '--apphost', appHostProject, '--non-interactive'],
      { encoding: 'utf8', maxBuffer: 1 << 26, stdio: ['ignore', 'pipe', 'ignore'], timeout: 60_000 })
  } catch {
    // The error would carry the output, and with it the code.
    throw new Error(`"${aspire} logs adminapi" failed: start the stack with scripts/Start-E2E.ps1 -Setup, and set ASPIRE_CLI when aspire is not on the path.`)
  }
  return [...output.matchAll(/Setup code: ([0-9A-Z-]{20,40})/g)].pop()?.[1] ?? null
}

/** Collects Content-Security-Policy violations of every page the test opens; the spec expects none (item 14). */
export function watchCsp(page: Page): string[] {
  const violations: string[] = []
  page.on('console', message => {
    if (/Content Security Policy|Content-Security-Policy/i.test(message.text())) violations.push(message.text())
  })
  return violations
}

let nextAdminRequest = 0

/** Keeps the single browser worker below the admin API's 120 requests a minute per user (Admin:RequestsPerMinute). */
async function paceAdminRequest(): Promise<void> {
  const slot = Math.max(Date.now(), nextAdminRequest)
  nextAdminRequest = slot + 550
  await new Promise(resolve => setTimeout(resolve, Math.max(0, slot - Date.now())))
}

export async function login(page: Page, role: Role): Promise<void> {
  await page.route('**/api/**', async route => {
    await paceAdminRequest()
    await route.continue()
  })
  await page.goto('/')
  await page.getByRole('link', { name: 'Sign in', exact: true }).click()
  await page.locator('#username').fill(role)
  await typeSecret(page.locator('#password'), appHostSecret('dev-user-password', 'E2E_PASSWORD'))
  await page.locator('#kc-login').click()
  await page.waitForURL(url => url.origin === new URL(adminUiUrl).origin)
  await expect(page.locator('#page-title')).toBeVisible()
  const user = await page.request.get('/bff/user')
  expect(user.ok()).toBe(true)
}

/** A paced GET of the admin API that fails on anything but 200 (a 429 must not read as an empty list). */
export async function getJson(page: Page, path: string): Promise<any> {
  await paceAdminRequest()
  const response = await page.request.get(path)
  expect(response.status(), `GET ${path}: ${await response.text()}`).toBe(200)
  return response.json()
}

async function send(page: Page, method: 'POST' | 'PUT' | 'DELETE', path: string, body?: unknown) {
  await paceAdminRequest()
  const xsrf = (await page.context().cookies()).find(cookie => cookie.name === 'RAG-XSRF-TOKEN')?.value
  if (!xsrf) throw new Error('Missing the RAG-XSRF-TOKEN cookie: call /bff/user first')
  return page.request.fetch(path, { method, data: body, headers: { 'X-Requested-With': 'XMLHttpRequest', 'X-XSRF-TOKEN': decodeURIComponent(xsrf) } })
}

export const post = (page: Page, path: string, body?: unknown) => send(page, 'POST', path, body)

/** The E2E stack seeds the fixture site's port in Settings.Crawl.AllowedPorts (connectors §7). Platform admin only. */
export async function expectFixturePortAllowed(page: Page): Promise<void> {
  const port = Number(new URL(fixtureSiteUrl).port)
  const crawl = await getJson(page, '/api/settings/crawl')
  expect(crawl.value.allowedPorts ?? [], 'Settings.Crawl.allowedPorts: start the stack with scripts/Start-E2E.ps1').toContain(port)
}

export async function ready(page: Page, path?: string): Promise<void> {
  if (path) await page.goto(path)
  else await page.reload()
  await expect(page.locator('#page-title')).toBeVisible()
  await expect(page.locator('main .animate-spin')).toHaveCount(0)
  await expect(page.locator('main [role="alert"]').filter({ has: page.getByRole('button', { name: 'Try again' }) })).toHaveCount(0)
  await page.evaluate(() => document.fonts.ready)
}

/**
 * Waits for the admin API to accept the POST that an action sends, set up before the action. Waiting on the page alone
 * can hide a refused or cancelled request until a later step times out.
 */
export async function accepted(page: Page, path: RegExp, action: () => Promise<void>): Promise<void> {
  const response = page.waitForResponse(r => r.request().method() === 'POST' && path.test(new URL(r.url()).pathname), { timeout: 60_000 })
  await action()
  const answered = await response
  expect(answered.status(), `${answered.url()}: ${await answered.text()}`).toBeLessThan(300)
}

/** Polls until the check passes; for work the worker does in the background (fetch, evaluation). */
export async function eventually(check: () => Promise<boolean>, message: string, timeout = 180_000): Promise<void> {
  await expect.poll(check, { message, timeout, intervals: [1_000, 2_000, 5_000] }).toBe(true)
}

/** True when the locator becomes visible within a few seconds: for checks inside {@link eventually} after a reload. */
export const visibleSoon = (locator: Locator, timeout = 8_000) => expect(locator).toBeVisible({ timeout }).then(() => true, () => false)

/** The assistant slugs that kalla-api lists for a client key. */
export async function listedModels(request: APIRequestContext, key: string): Promise<string[]> {
  const response = await request.get(`${apiUrl}/v1/models`, { headers: { Authorization: `Bearer ${key}` } })
  expect(response.status()).toBe(200)
  return ((await response.json()).data as { id: string }[]).map(model => model.id)
}

// WCAG 2.2 AA is the design target (admin design §10.1, docs/accessibility-statement.md); AAA rules are not part of the gate.
const wcagTags = [...new Set(axe.getRules().flatMap(rule => rule.tags))].filter(tag => /^wcag(2|21|22)(a|aa)$/.test(tag))

/** The page as it is passes the WCAG A/AA axe rules and target size; soft, so one run lists every failure. */
export async function scan(page: Page, label: string): Promise<void> {
  // A button whose variant just changed is mid-transition between two colour pairs, which axe would read as low contrast.
  await page.waitForFunction(() => !document.getAnimations().some(a => a instanceof CSSTransition && a.playState === 'running'))
  const result = await new AxeBuilder({ page }).withTags(wcagTags).analyze()
  const failures = result.violations.map(v => ({ id: v.id, targets: v.nodes.map(node => node.target) }))
  expect.soft(failures, `axe: ${label}`).toEqual([])
  expect.soft(await undersizedControls(page), `target size (WCAG 2.5.8): ${label}`).toEqual([])
}

/**
 * Targets that fail WCAG 2.2 SC 2.5.8 (Target Size, Minimum, AA). Copied from the gateway: a target passes when it is at
 * least 24 x 24 px, or when a 24 px circle centred on it does not touch any other target. Checkboxes and radios are
 * skipped: their label is the target; so are visually hidden (`sr-only`) controls, which no pointer can hit (#1064).
 */
export async function undersizedControls(page: Page, minimum = 24) {
  return page.evaluate(min => {
    const selector = 'button, a[href], input:not([type="hidden"]), select, textarea, summary, [role="button"], [tabindex]:not([tabindex="-1"])'
    // Visually hidden (`sr-only`: clipped to nothing) is not a pointer target: a file input behind its drop zone and
    // buttons, say (#1064). It stays reachable by keyboard, which this check is not about.
    // Tailwind's `sr-only` is 1 x 1 px with `clip-path: inset(50%)` (the file inputs measured 1 x 1 with `clip: auto`), so a
    // control of at most 1 x 1 px counts as hidden too: nothing is designed as a 1 px target.
    const clipped = (style: CSSStyleDeclaration, rect: DOMRect) =>
      /rect\(0(px)?,? 0(px)?,? 0(px)?,? 0(px)?\)/.test(style.clip) || style.clipPath === 'inset(50%)' || (rect.width <= 1 && rect.height <= 1)
    const visible = (element: Element) => {
      const rect = element.getBoundingClientRect()
      const style = getComputedStyle(element)
      return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && !clipped(style, rect) && !element.closest('[aria-hidden="true"], [inert]')
    }
    const root = [...document.querySelectorAll('dialog[open]')].filter(d => d.matches(':modal')).pop() ?? document
    const all = [...root.querySelectorAll(selector)].filter(visible)
    const rects = all.map(element => ({ element, rect: element.getBoundingClientRect() }))
    const failures: { id: string; text: string; width: number; height: number }[] = []
    for (const { element, rect } of rects) {
      if (element.matches('input[type="checkbox"], input[type="radio"]') || rect.width >= min && rect.height >= min) continue
      const cx = rect.left + rect.width / 2
      const cy = rect.top + rect.height / 2
      const crowded = rects.some(other => {
        if (other.element === element || other.element.contains(element) || element.contains(other.element)) return false
        const dx = Math.max(other.rect.left - cx, 0, cx - other.rect.right)
        const dy = Math.max(other.rect.top - cy, 0, cy - other.rect.bottom)
        return Math.hypot(dx, dy) < min / 2
      })
      if (crowded) failures.push({ id: element.id, text: (element.textContent ?? '').trim().slice(0, 30), width: Math.round(rect.width), height: Math.round(rect.height) })
    }
    return failures
  }, minimum)
}

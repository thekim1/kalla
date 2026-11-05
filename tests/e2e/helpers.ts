import { readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { expect, type APIRequestContext, type Locator, type Page } from '@playwright/test'

export type Role = 'rag-platform-admin' | 'rag-department-admin' | 'rag-viewer' | 'rag-no-role'

export const adminUiUrl = process.env.ADMIN_UI_URL ?? 'https://localhost:5174'
export const apiUrl = process.env.RAG_API_URL ?? 'https://localhost:7401'
/** The static Swedish website of fixture-site/ (scripts/Start-E2E.ps1 serves it on this port). */
export const fixtureSiteUrl = process.env.FIXTURE_SITE_URL ?? 'http://127.0.0.1:7416'

function appHostSecret(name: string, environment: string): string {
  const fromEnvironment = process.env[environment]
  if (fromEnvironment) return fromEnvironment
  const directory = process.env.APPDATA
    ? join(process.env.APPDATA, 'Microsoft', 'UserSecrets')
    : join(homedir(), '.microsoft', 'usersecrets')
  const secrets = JSON.parse(readFileSync(join(directory, 'ume-rag-apphost-dev', 'secrets.json'), 'utf8').replace(/^﻿/, ''))
  const value: unknown = secrets[`Parameters:${name}`]
  if (typeof value !== 'string' || !value) throw new Error(`Start the AppHost to generate ${name}, or set ${environment}.`)
  return value
}

/** The seeded Open WebUI (dev) client's key (DevSeeder: ume-rag-sk- + the AppHost's dev-key). */
export const devClientKey = () => `ume-rag-sk-${appHostSecret('dev-key', 'E2E_DEV_KEY')}`

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
  // evaluate keeps the password out of locator call logs if a later assertion fails.
  await page.locator('#password').evaluate((element, password) => {
    const input = element as HTMLInputElement
    input.value = password
    input.dispatchEvent(new Event('input', { bubbles: true }))
  }, appHostSecret('dev-user-password', 'E2E_PASSWORD'))
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

/** The assistant slugs that rag-api lists for a client key. */
export async function listedModels(request: APIRequestContext, key: string): Promise<string[]> {
  const response = await request.get(`${apiUrl}/v1/models`, { headers: { Authorization: `Bearer ${key}` } })
  expect(response.status()).toBe(200)
  return ((await response.json()).data as { id: string }[]).map(model => model.id)
}

/**
 * Targets that fail WCAG 2.2 SC 2.5.8 (Target Size, Minimum, AA). Copied from the gateway: a target passes when it is at
 * least 24 x 24 px, or when a 24 px circle centred on it does not touch any other target. Checkboxes and radios are
 * skipped: their label is the target.
 */
export async function undersizedControls(page: Page, minimum = 24) {
  return page.evaluate(min => {
    const selector = 'button, a[href], input:not([type="hidden"]), select, textarea, summary, [role="button"], [tabindex]:not([tabindex="-1"])'
    const visible = (element: Element) => {
      const rect = element.getBoundingClientRect()
      const style = getComputedStyle(element)
      return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && !element.closest('[aria-hidden="true"], [inert]')
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

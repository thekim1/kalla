import AxeBuilder from '@axe-core/playwright'
import axe from 'axe-core'
import { expect, test, type Page } from '@playwright/test'
import { getJson, login, ready, undersizedControls, watchCsp } from '../helpers'

// WCAG 2.2 AA is the design target (admin design §10.1, docs/accessibility-statement.md); AAA rules are not part of the gate.
const tags = [...new Set(axe.getRules().flatMap(rule => rule.tags))].filter(tag => /^wcag(2|21|22)(a|aa)$/.test(tag))
const themes = ['light', 'dark', 'lumen'] as const
const themeStorageKey = 'ume-theme'

/** Every page of src/admin-ui/src/router (and platform/routes.ts), plus the 404 page. Keep in sync with the router. */
const listPages = ['/', '/collections', '/assistants', '/feedback', '/audit', '/organisation', '/plattform',
  '/plattform/clients', '/plattform/keys', '/plattform/settings', '/plattform/data-protection', '/plattform/site-profiles',
  '/plattform/gateway-models', '/plattform/organisation', '/forbidden', '/no-such-page']

test.afterEach(async ({ page }) => { await page.close() })

async function scan(page: Page, label: string): Promise<void> {
  const result = await new AxeBuilder({ page }).withTags(tags).analyze()
  const failures = result.violations.map(v => ({ id: v.id, targets: v.nodes.map(node => node.target) }))
  expect.soft(failures, `axe: ${label}`).toEqual([])
  expect.soft(await undersizedControls(page), `target size (WCAG 2.5.8): ${label}`).toEqual([])
}

/** The list pages plus one of each detail page, when the pilot journey (or earlier data) has made one. */
async function allPages(page: Page): Promise<string[]> {
  const pages = [...listPages]
  const first = async (path: string) => ((await getJson(page, path)).items ?? [])[0]
  const collection = await first('/api/collections')
  if (collection) {
    pages.push(`/collections/${collection.id}`)
    const sources = await getJson(page, `/api/collections/${collection.id}/sources`)
    if (sources[0]) {
      pages.push(`/sources/${sources[0].id}`)
      if (sources[0].lastRun) pages.push(`/runs/${sources[0].lastRun.id}`)
    }
  }
  const assistant = await first('/api/assistants')
  if (assistant) pages.push(`/assistants/${assistant.id}`)
  expect(pages.slice(listPages.length), 'detail pages to scan: knowledge base, source, run, assistant (the pilot journey makes them)').toHaveLength(4)
  return pages
}

for (const theme of themes) {
  test(`every page passes the WCAG A/AA axe rules and target size in ${theme}`, async ({ page }) => {
    const csp = watchCsp(page)
    await login(page, 'rag-platform-admin')
    await page.evaluate(([key, value]) => localStorage.setItem(key, value), [themeStorageKey, theme])
    for (const path of await allPages(page)) {
      await ready(page, path)
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme)
      await scan(page, `${theme}: ${path}`)
    }
    expect(csp, 'Content-Security-Policy violations').toEqual([])
  })
}

test('drawers and dialogs pass axe', async ({ page }) => {
  await login(page, 'rag-department-admin')
  const open = async (label: string, trigger: () => Promise<void>, dialogName: string) => {
    await trigger()
    const dialog = page.getByRole('dialog', { name: dialogName })
    await expect(dialog).toBeVisible()
    await scan(page, label)
    await page.keyboard.press('Escape')
    await expect(dialog).toBeHidden()
  }
  await ready(page, '/collections')
  await open('create knowledge base drawer', () => page.getByRole('main').getByRole('button', { name: 'Create knowledge base' }).click(), 'Create knowledge base')
  await ready(page, '/assistants')
  await open('create assistant drawer', () => page.getByRole('main').getByRole('button', { name: 'Create assistant' }).click(), 'Create assistant')
  const collection = ((await getJson(page, '/api/collections')).items ?? [])[0]
  if (collection) {
    await ready(page, `/collections/${collection.id}`)
    await page.getByRole('tab', { name: /^Sources/ }).click()
    await open('add source drawer', () => page.getByRole('main').getByRole('button', { name: 'Add source' }).click(), 'Add source')
  }
})

test('all pages reflow at 320px and at 200 percent text size', async ({ page }) => {
  await login(page, 'rag-platform-admin')
  const pages = await allPages(page)
  for (const width of [320, 640]) {
    await page.setViewportSize({ width, height: 900 })
    for (const path of pages) {
      await ready(page, path)
      if (width === 640) await page.evaluate(() => { document.documentElement.style.fontSize = '200%' })
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1)
      expect.soft(overflow, `${width}px: ${path}`).toBe(false)
    }
  }
})

test('keyboard: landmarks, page heading focus after navigation, themes by arrow keys, skip link', async ({ page }) => {
  await login(page, 'rag-department-admin')
  await ready(page, '/')
  await expect(page.getByRole('main')).toHaveCount(1)
  await expect(page.getByRole('navigation', { name: 'Main menu' })).toBeVisible()

  await page.getByRole('navigation', { name: 'Main menu' }).getByRole('link', { name: 'Knowledge bases' }).click()
  await expect(page.locator('#page-title')).toBeFocused()

  const group = page.getByRole('radiogroup', { name: 'Theme' })
  await group.getByRole('radio', { checked: true }).focus()
  const before = await page.locator('html').getAttribute('data-theme')
  await page.keyboard.press('ArrowRight')
  await expect(page.locator('html')).not.toHaveAttribute('data-theme', before!)
  await expect(group.getByRole('radio', { checked: true })).toBeFocused()

  await page.setViewportSize({ width: 390, height: 800 })
  await ready(page, '/')
  await page.keyboard.press('Tab')
  await expect(page.getByRole('link', { name: 'Skip to main content' })).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page.locator('#main')).toBeFocused()
})

test('forced colours and reduced motion keep a visible focus indicator', async ({ page }) => {
  await login(page, 'rag-department-admin')
  await page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' })
  await ready(page, '/collections')
  const outline = await page.getByRole('searchbox', { name: 'Search knowledge bases' }).evaluate(element => {
    (element as HTMLElement).focus()
    return getComputedStyle(element).outlineStyle
  })
  expect(outline).not.toBe('none')
})

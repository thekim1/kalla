import { expect, test, type Page } from '@playwright/test'
import { getJson, login, ready, scan, watchCsp } from '../helpers'

const themes = ['light', 'dark', 'lumen'] as const
const themeStorageKey = 'ume-theme'

/** Every page of src/admin-ui/src/router (and platform/routes.ts), plus the 404 page. Keep in sync with the router. */
const listPages = ['/', '/map', '/collections', '/assistants', '/feedback', '/audit', '/organisation',
  '/plattform/clients', '/plattform/clients?tab=keys', '/plattform/settings', '/plattform/settings/crawl', '/plattform/settings/data-protection',
  '/plattform/settings/site-profiles', '/plattform/gateway-models', '/forbidden', '/no-such-page']

test.afterEach(async ({ page }) => { await page.close() })

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

test('the ⋯ menu passes axe open, as a menu on a desktop and as a sheet on a phone, and Esc returns focus (admin design §5.12)', async ({ page }) => {
  await login(page, 'rag-department-admin')
  const assistant = ((await getJson(page, '/api/assistants')).items ?? [])[0]
  test.skip(!assistant, 'needs an assistant (the pilot journey makes one)')
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 900 })
    await ready(page, `/assistants/${assistant.id}`)
    const more = page.getByRole('button', { name: /^More actions for / })
    await expect(more).toHaveAttribute('aria-haspopup', 'menu')
    await more.click()
    await expect(more).toHaveAttribute('aria-expanded', 'true')
    if (width === 1280) {
      const menu = page.getByRole('menu')
      await expect(menu).toBeVisible()
      await expect(menu.getByRole('menuitem').first()).toBeFocused()
    } else {
      await expect(page.getByRole('dialog', { name: /^More actions for / })).toBeVisible()
    }
    await scan(page, `the ⋯ menu at ${width}px`)
    await page.keyboard.press('Escape')
    await expect(more).toBeFocused()
    await expect(more).toHaveAttribute('aria-expanded', 'false')
    // A drawer opened from the menu returns focus to the ⋯ button when it closes (WCAG 2.4.3).
    await more.click()
    await (width === 1280 ? page.getByRole('menuitem', { name: 'Edit name and contact' }) : page.getByRole('button', { name: 'Edit name and contact' })).click()
    await expect(page.getByRole('dialog', { name: 'Edit assistant' })).toBeVisible()
    await page.keyboard.press('Escape')
    await expect(more).toBeFocused()
  }
  // A row's menu is not clipped by its table: the oldest version's Restore is on screen.
  await page.setViewportSize({ width: 1280, height: 900 })
  await ready(page, `/assistants/${assistant.id}?tab=versions`)
  const rowMenus = page.getByRole('button', { name: /^More actions for version / })
  if (await rowMenus.count()) {
    await rowMenus.last().click()
    await expect(page.getByRole('menuitem', { name: /Restore/ })).toBeInViewport({ ratio: 1 })
    await page.keyboard.press('Escape')
  }
})

test('the save bar passes axe, and a focused field is never hidden behind it (admin design §5.12, Saving)', async ({ page }) => {
  await login(page, 'rag-department-admin')
  const assistant = ((await getJson(page, '/api/assistants')).items ?? [])[0]
  test.skip(!assistant, 'needs an assistant (the pilot journey makes one)')
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 700 })
    await ready(page, `/assistants/${assistant.id}`)
    // The page fills the draft form after its requests settle; an edit before that is overwritten.
    await page.waitForLoadState('networkidle')
    const prompt = page.getByRole('textbox', { name: 'Instructions' })
    await prompt.fill(`${await prompt.inputValue()} `)
    const bar = page.getByRole('region', { name: 'Unsaved changes' })
    await expect(bar).toBeVisible()
    await scan(page, `the save bar at ${width}px`)
    // The last field above the bar, focused: scrolled clear of the bar (scroll-padding-bottom).
    const fields = page.getByRole('main').locator('input:not([type=hidden]):not([type=checkbox]):not([type=radio]), textarea, select')
    const last = fields.filter({ visible: true }).last()
    await last.focus()
    const [field, barBox] = [await last.boundingBox(), await bar.boundingBox()]
    if (field && barBox) expect(field.y + field.height, `${width}px: the focused field ends above the bar`).toBeLessThanOrEqual(barBox.y + 1)
    await page.getByRole('button', { name: 'Discard' }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Discard' }).click()
    await expect(bar).toBeHidden()
  }
})

test('the assistant page passes axe on each of its five tabs, and the test as a sheet on a phone (admin design §5.5.4, B-78)', async ({ page }) => {
  await login(page, 'rag-department-admin')
  const assistant = ((await getJson(page, '/api/assistants')).items ?? [])[0]
  test.skip(!assistant, 'needs an assistant (the pilot journey makes one)')
  await page.setViewportSize({ width: 1280, height: 900 })
  for (const tab of ['Build', 'Quality', 'Versions', 'Access', 'Usage']) {
    await ready(page, `/assistants/${assistant.id}${tab === 'Build' ? '' : `?tab=${tab.toLowerCase()}`}`)
    await expect(page.getByRole('tab', { name: tab, exact: true })).toHaveAttribute('aria-selected', 'true')
    await page.waitForLoadState('networkidle')
    await scan(page, `the assistant's ${tab} tab`)
  }
  // Build at 1,280 px: the test panel beside the draft; on a phone, Try the draft opens it as a sheet.
  await ready(page, `/assistants/${assistant.id}`)
  await expect(page.getByRole('textbox', { name: 'Question' })).toBeVisible()
  await page.setViewportSize({ width: 390, height: 844 })
  await expect(page.getByRole('textbox', { name: 'Question' })).toBeHidden()
  await page.getByRole('button', { name: 'Try the draft' }).click()
  const sheet = page.getByRole('dialog', { name: 'Try the draft' })
  await expect(sheet.getByRole('textbox', { name: 'Question' })).toBeVisible()
  await scan(page, 'the test sheet at 390px')
  await page.keyboard.press('Escape')
  await expect(page.getByRole('button', { name: 'Try the draft' })).toBeFocused()
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
  const outline = await page.getByRole('searchbox', { name: 'Search knowledge bases by name' }).evaluate(element => {
    (element as HTMLElement).focus()
    return getComputedStyle(element).outlineStyle
  })
  expect(outline).not.toBe('none')
})

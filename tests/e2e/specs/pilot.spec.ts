import { expect, test, type Page } from '@playwright/test'
import {
  accepted, adminUiUrl, expectFixturePortAllowed, getJson, devClientKey, eventually, fixtureSiteUrl, listedModels, login, ready, visibleSoon, watchCsp,
} from '../helpers'

// The pilot department admin's journey (admin design §5.4 steps 1–13, §11.4) against the static fixture site, on the
// FakeGateway with the real worker and parser. Run twice: with the mouse, and with the keyboard only. Step 14 (P1b item
// 36): the knowledge base is shared with the whole municipality and another department's assistant links it with a topic.

test.afterEach(async ({ page }) => { await page.close() })

test.beforeAll(async ({ browser }) => {
  const page = await browser.newPage()
  await login(page, 'rag-platform-admin')
  await expectFixturePortAllowed(page)
  await page.close()
})

for (const mode of ['mouse', 'keyboard'] as const) {
  test(`pilot journey with the ${mode}`, async ({ page, browser }) => {
    const csp = watchCsp(page)
    const suffix = crypto.randomUUID().slice(0, 8)
    const collection = `Pilot ${mode} ${suffix}`
    const act = actor(page, mode)

    // 1 Sign in to the start page. Its "Get started" checklist shows only while a knowledge base is not done yet (§5.4), so
    // it depends on what earlier runs left.
    await login(page, 'rag-department-admin')
    await expect(page.getByRole('heading', { name: /^Welcome, /, level: 1 })).toBeVisible()

    // 2 Create the knowledge base: name, department, information class (none preselected), what goes in.
    await act.press(page.getByRole('navigation', { name: 'Main menu' }).getByRole('link', { name: 'Knowledge bases' }))
    await expect(page.locator('#page-title')).toHaveText('Knowledge bases')
    await act.press(page.getByRole('main').getByRole('button', { name: 'Create knowledge base' }))
    const create = page.getByRole('dialog', { name: 'Create knowledge base' })
    await act.fill(create.getByRole('textbox', { name: 'Name', exact: true }), collection)
    await act.select(create.getByRole('combobox', { name: 'Department' }), { label: 'Kommunledningskontoret' })
    await expect(create.getByRole('radio', { checked: true })).toHaveCount(0)
    await act.check(create.getByRole('radio', { name: /^Public/ }))
    // What goes in (required since the one-step create, #532): a website, whose source is added on the next page.
    await act.check(create.getByRole('radio', { name: /^A website or a list of addresses/ }))
    await act.press(create.getByRole('button', { name: 'Create knowledge base' }))
    await expect(create).toBeHidden()
    await expect(page.locator('#page-title')).toHaveText(collection)

    // 3 Add a website source.
    await act.press(page.getByRole('tab', { name: 'Sources' }))
    await act.press(page.getByRole('main').getByRole('button', { name: 'Add source' }))
    const source = page.getByRole('dialog', { name: 'Add source' })
    await act.check(source.getByRole('radio', { name: /^Website/ }))
    await act.fill(source.getByRole('textbox', { name: 'Name of the source' }), 'Testwebbplatsen')
    await act.fill(source.getByRole('textbox', { name: 'Start addresses' }), `${fixtureSiteUrl}/webbplats/`)
    // The saved umea.se pages link to paths the fixture site does not have: keep the crawl inside its part.
    await act.press(source.getByText('More settings'))
    await act.fill(source.getByRole('textbox', { name: 'Only include' }), `${new URL(fixtureSiteUrl).host}/webbplats/**`)

    // 4 Check: reachability, robots, sitemap; nothing blocks (the E2E stack seeds the fixture port in Settings.Crawl).
    await act.press(source.getByRole('button', { name: 'Check' }))
    const checks = source.getByRole('region', { name: 'Result of the check' })
    await expect(checks.getByText(/^About \d+ pages$/)).toBeVisible({ timeout: 120_000 })
    await expect(checks.getByText('Blocks', { exact: true }), await checks.innerText()).toHaveCount(0)

    // 5 Preview: Before / After per page and each step's output.
    await act.press(source.getByRole('button', { name: 'Preview' }))
    const preview = source.getByRole('region', { name: 'Result of the preview' })
    await expect(preview.getByText('Before / After')).toBeVisible({ timeout: 180_000 })

    // 6 Save and fetch: the source page, then the run page, which counts the documents until the run is done.
    await accepted(page, /^\/api\/collections\/[^/]+\/sources$/, () => act.press(source.getByRole('button', { name: 'Save and fetch' })))
    await expect(source).toBeHidden()
    await expect(page.locator('#page-title')).toHaveText('Testwebbplatsen')
    const runs = page.getByRole('table', { name: 'Runs' })
    await act.press(runs.getByRole('row').nth(1).getByRole('link').first())
    await expect(page.locator('#page-title')).toHaveText(/Run/)
    await eventually(async () => {
      await ready(page)
      return visibleSoon(page.getByRole('main').getByText(/^(Done|Done with remarks)$/).first())
    }, 'the first fetch finishes', 300_000)
    await expect(page.getByText('The whole website was gone through.')).toBeVisible()
    await act.press(page.getByRole('link', { name: 'Back to the source' }))

    // 7 Test questions: one of our own, with the document that answers it.
    const collectionPage = await collectionUrl(page, collection)
    await ready(page, collectionPage)
    await act.press(page.getByRole('tab', { name: 'Test questions and quality' }))
    await act.press(page.getByRole('button', { name: 'Add question' }))
    const question = page.getByRole('dialog', { name: 'New test question' })
    await act.fill(question.getByRole('textbox', { name: 'Question', exact: true }), 'Vad kostar det att hyra en isyta?')
    await act.fill(question.getByRole('textbox', { name: 'Search documents by title' }), 'Isytor')
    await act.press(question.getByRole('button', { name: 'Add Isytor' }).first())
    await accepted(page, /\/evaluation\/questions$/, () => act.press(question.getByRole('button', { name: 'Save' })))
    await expect(question).toBeHidden()
    await eventually(async () => {
      await ready(page)
      await page.getByRole('tab', { name: 'Test questions and quality' }).click()
      return visibleSoon(page.getByRole('rowheader', { name: 'Vad kostar det att hyra en isyta?' }))
    }, 'the test question is saved')
    await accepted(page, /\/evaluation\/run$/, () => act.press(page.getByRole('button', { name: 'Run evaluation' })))
    // The tab follows the job and shows the report when it is done; a reload here would only hide that.
    await expect(page.getByText(/^Evaluation · /)).toBeVisible({ timeout: 180_000 })

    // Search settings: the FakeGateway's embeddings are hashes, so every distance is large; the widest coverage threshold
    // lets the draft answer instead of "found nothing" (a real model would not need this).
    await act.press(page.getByRole('tab', { name: 'Search settings' }))
    await act.fill(page.getByRole('textbox', { name: 'Largest distance' }), '1')
    await act.press(page.getByRole('button', { name: 'Save search settings' }))
    await expect(page.getByText(/The search settings have been saved/)).toBeVisible()

    // 8 Publish the knowledge base.
    await act.press(page.getByRole('button', { name: 'Publish', exact: true }))
    await expect(page.getByText('Published', { exact: true }).first()).toBeVisible()

    // 9 Create the assistant: name, department, level, description and contact; then the draft's model and knowledge.
    const assistant = `Pilotassistent ${mode} ${suffix}`
    await act.press(page.getByRole('navigation', { name: 'Main menu' }).getByRole('link', { name: 'Assistants' }))
    await act.press(page.getByRole('main').getByRole('button', { name: 'Create assistant' }))
    const drawer = page.getByRole('dialog', { name: 'Create assistant' })
    await act.fill(drawer.getByRole('textbox', { name: 'Name', exact: true }), assistant)
    await act.select(drawer.getByRole('combobox', { name: 'Department' }), { label: 'Kommunledningskontoret' })
    await act.check(drawer.getByRole('radio', { name: /^Public/ }))
    await act.fill(drawer.getByRole('textbox', { name: 'Description' }), 'Svarar på frågor om testwebbplatsen.')
    await act.fill(drawer.getByRole('textbox', { name: 'Contact person', exact: true }), 'Anna Andersson')
    await act.fill(drawer.getByRole('textbox', { name: 'Contact person’s email' }), 'anna.andersson@example.invalid')
    await act.press(drawer.getByRole('button', { name: 'Create assistant' }))
    await expect(drawer).toBeHidden()
    await expect(page.locator('#page-title')).toHaveText(assistant)
    // The first draft starts from the platform's default instructions (item 39); the model is under Advanced.
    await expect(page.getByRole('textbox', { name: 'Instructions' })).not.toHaveValue('')
    await act.press(page.getByText('Advanced: model, follow-up questions, history, length'))
    const model = page.getByRole('combobox', { name: 'Language model' })
    await expect(model.locator('option', { hasText: 'ume/chat-eu' })).toHaveCount(1)
    await act.select(model, { label: 'ume/chat-eu' })
    await act.press(page.getByRole('button', { name: 'Add knowledge base', exact: true }))
    const picker = page.getByRole('dialog', { name: 'Add a knowledge base' })
    await act.press(picker.getByRole('button', { name: `Add ${collection}` }))
    await expect(picker).toBeHidden()
    await act.press(page.getByRole('button', { name: 'Save draft' }))
    await expect(page.getByText('The draft has been saved.')).toBeVisible()

    // 10 Test the draft: an answer with a cited source.
    await act.press(page.getByRole('tab', { name: 'Test' }))
    await act.fill(page.getByRole('textbox', { name: 'Question' }), 'Vad kostar det att hyra en isyta?')
    await act.press(page.getByRole('button', { name: 'Test', exact: true }))
    const answer = page.getByTestId('answer')
    await expect(answer.getByText('Answered', { exact: true })).toBeVisible({ timeout: 60_000 })
    await expect(page.getByText('Cited', { exact: true }).first()).toBeVisible()

    // 11 Publish the assistant. It runs on its department's AI gateway key: while the gateway acts as keys, publish needs one
    // that reaches its models (V39); the E2E stack's FakeGateway has "Digitalisering – pilot" for that (AppHost).
    await act.press(page.getByRole('button', { name: 'Edit details' }))
    const details = page.getByRole('dialog', { name: 'Edit assistant' })
    await act.select(details.getByRole('combobox', { name: 'AI gateway key' }), { label: 'Digitalisering – pilot' })
    await act.press(details.getByRole('button', { name: 'Save', exact: true }))
    await expect(details).toBeHidden()
    await act.press(page.getByRole('button', { name: 'Publish', exact: true }))
    await expect(page.getByText('Published: version 1')).toBeVisible()

    // 12 Make it available in Open WebUI.
    await act.press(page.getByRole('tab', { name: 'Audience' }))
    await act.check(page.getByRole('checkbox', { name: 'Open WebUI (dev)' }))
    await act.press(page.getByRole('button', { name: 'Save audience' }))
    await expect(page.getByText('The audience has been saved.')).toBeVisible()

    // 13 Open it in Open WebUI: the assistant page links to the client's PortalUrl (the dev client's is the stack's Open
    // WebUI) in a new tab, and rag-api offers the assistant's model name to the client's key.
    const portal = page.getByRole('link', { name: 'Open in Open WebUI (dev) (opens in a new tab)' })
    const portalUrl = new URL((await portal.getAttribute('href'))!)
    // The link opens a new tab; headless Chromium on the runner stopped emitting that tab after 2026-11-08 (issue filed),
    // so the test checks the link's target and opens its address itself.
    await expect(portal).toHaveAttribute('target', '_blank')
    await expect(portal).toHaveAttribute('rel', /noopener/)
    const openWebUi = await page.context().newPage()
    // Open WebUI starts after rag-api and migrates its own store first: give it up to three minutes to answer.
    await eventually(() => openWebUi.goto(portalUrl.href, { timeout: 20_000 }).then(() => true, () => false), 'Open WebUI answers', 180_000)
    await expect(openWebUi).toHaveURL(url => url.origin === portalUrl.origin)
    await expect(openWebUi).toHaveTitle(/Open WebUI/, { timeout: 30_000 })
    await openWebUi.close()
    const slug = (await page.getByText(/^Model name in Open WebUI: /).textContent())!.replace('Model name in Open WebUI: ', '').trim()
    await eventually(async () => (await listedModels(page.request, devClientKey())).includes(slug), 'rag-api lists the assistant', 60_000)

    // 14 Share the knowledge base with the whole municipality (admin design §5.5.2, item 36).
    await ready(page, collectionPage)
    await act.press(page.getByRole('tab', { name: 'Sharing' }))
    await act.check(page.getByRole('radio', { name: 'Whole municipality' }))
    await act.press(page.getByRole('button', { name: 'Save sharing' }))
    await expect(page.getByText('The sharing has been saved.')).toBeVisible()

    // An assistant of another department (Fritidsförvaltningen; the platform administrator works in every department)
    // links it from "Shared with us" with a topic, and V11 has nothing to say.
    const otherContext = await browser.newContext({ baseURL: adminUiUrl, ignoreHTTPSErrors: true, viewport: { width: 1280, height: 900 } })
    const other = await otherContext.newPage()
    const otherAct = actor(other, mode)
    await login(other, 'rag-platform-admin')
    await otherAct.press(other.getByRole('navigation', { name: 'Main menu' }).getByRole('link', { name: 'Assistants' }))
    await otherAct.press(other.getByRole('main').getByRole('button', { name: 'Create assistant' }))
    const otherDrawer = other.getByRole('dialog', { name: 'Create assistant' })
    await otherAct.fill(otherDrawer.getByRole('textbox', { name: 'Name', exact: true }), `Fritidsassistent ${mode} ${suffix}`)
    await otherAct.select(otherDrawer.getByRole('combobox', { name: 'Department' }), { label: 'Fritidsförvaltningen' })
    await otherAct.check(otherDrawer.getByRole('radio', { name: /^Public/ }))
    await otherAct.press(otherDrawer.getByRole('button', { name: 'Create assistant' }))
    await expect(otherDrawer).toBeHidden()
    await otherAct.press(other.getByRole('button', { name: 'Add knowledge base', exact: true }))
    const otherPicker = other.getByRole('dialog', { name: 'Add a knowledge base' })
    await otherAct.press(otherPicker.getByRole('tab', { name: 'Shared with us' }))
    await otherAct.press(otherPicker.getByRole('button', { name: `Add ${collection}` }))
    await expect(otherPicker).toBeHidden()
    const link = other.getByRole('listitem').filter({ hasText: collection })
    await expect(link.getByText('(Kommunledningskontoret, shared)')).toBeVisible()
    await otherAct.fill(link.getByRole('combobox', { name: `Topics of ${collection}` }), 'isytor')
    await otherAct.press(link.getByRole('button', { name: 'Add topic' }))
    await expect(link.getByText('isytor', { exact: true })).toBeVisible()
    await otherAct.press(other.getByRole('button', { name: 'Save draft' }))
    await expect(other.getByText('The draft has been saved.')).toBeVisible()
    await expect(other.getByText(/is not shared with yours/)).toHaveCount(0)
    await expect(other.getByText(/A topic is invalid/)).toHaveCount(0)
    await otherContext.close()

    expect(csp, 'Content-Security-Policy violations').toEqual([])
  })
}

/** Mouse: click, fill, select. Keyboard: focus by Tab from the page heading, then type and press keys. */
function actor(page: Page, mode: 'mouse' | 'keyboard') {
  type Target = ReturnType<Page['getByRole']>
  const focus = async (target: Target) => {
    // A control that is not on the page fails here, not after hundreds of Tabs that each wait for it.
    await expect(target).toBeVisible()
    // Tab until the target has focus; a keyboard-only user reaches every control in at most a few hundred steps.
    for (let i = 0; i < 300; i++) {
      if (await target.evaluate(element => element === document.activeElement).catch(() => false)) return
      await page.keyboard.press('Tab')
    }
    throw new Error(`Not reachable by Tab: ${target}`)
  }
  return {
    async press(target: Target) {
      if (mode === 'mouse') return target.click()
      if (await target.getAttribute('role') === 'tab') {
        // ARIA tabs: Tab reaches the selected tab only; the arrow keys move between the tabs.
        // The tab's own tab list, whatever the target locator starts from (a `has:` filter would search inside the list).
        const tablist = target.locator('xpath=ancestor::*[@role="tablist"][1]')
        await focus(tablist.getByRole('tab', { selected: true }))
        for (let i = 0; i < 10 && !await target.evaluate(element => element === document.activeElement); i++) {
          const before = await page.evaluate(() => document.activeElement?.id)
          await page.keyboard.press('ArrowRight')
          // The tab list moves focus on the next animation frame.
          await expect.poll(() => page.evaluate(() => document.activeElement?.id)).not.toBe(before)
        }
        await expect(target).toBeFocused()
      } else {
        await focus(target)
      }
      await page.keyboard.press('Enter')
    },
    async fill(target: Target, value: string) {
      if (mode === 'mouse') return target.fill(value)
      await focus(target)
      await page.keyboard.press('ControlOrMeta+a')
      await page.keyboard.type(value)
    },
    async check(target: Target) {
      if (mode === 'mouse') return target.check()
      const radioGroup = await target.evaluate(element =>
        element instanceof HTMLInputElement && element.type === 'radio' ? element.name : null)
      if (radioGroup) {
        // Native radio group: Tab reaches the checked radio (the first one while none is checked); the arrow keys move
        // between the radios and check the one they land on.
        const group = page.locator(`input[type="radio"][name="${radioGroup}"]`)
        const checked = group.and(page.locator(':checked'))
        await focus(await checked.count() > 0 ? checked : group.first())
        for (let i = 0; i < 10 && !await target.evaluate(element => element === document.activeElement); i++) {
          await page.keyboard.press('ArrowDown')
        }
        await expect(target).toBeFocused()
        if (!await target.isChecked()) await page.keyboard.press('Space')
      } else {
        await focus(target)
        await page.keyboard.press('Space')
      }
      await expect(target).toBeChecked()
    },
    async select(target: Target, option: { label: string }) {
      if (mode === 'mouse') return void await target.selectOption(option)
      await expect(target.locator('option', { hasText: option.label })).toHaveCount(1)
      await focus(target)
      const labels = await target.locator('option').allTextContents()
      const current = await target.evaluate(element => (element as HTMLSelectElement).selectedIndex)
      const wanted = labels.findIndex(label => label.trim() === option.label)
      if (wanted < 0) throw new Error(`No option ${option.label}`)
      for (let i = current; i < wanted; i++) await page.keyboard.press('ArrowDown')
      for (let i = current; i > wanted; i--) await page.keyboard.press('ArrowUp')
      await expect(target.locator('option:checked')).toHaveText(option.label)
    },
  }
}

async function collectionUrl(page: Page, name: string): Promise<string> {
  const found = ((await getJson(page, `/api/collections?q=${encodeURIComponent(name)}`)).items as { id: string; name: string }[]).find(c => c.name === name)
  if (!found) throw new Error(`No knowledge base ${name}`)
  return `/collections/${found.id}`
}

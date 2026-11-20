import { expect, test, type BrowserContext, type Locator, type Page } from '@playwright/test'
import { appHostSecret, ready, scan, setupCode, typeSecret } from '../helpers'

// The setup wizard (admin design §5.11, identity design §19) end to end on the stack of `scripts/Start-E2E.ps1 -Setup`: a fresh
// database with Setup:Try, so kalla-admin starts without a sign-in and the wizard opens. Finishing closes setup, so the tests
// run in order in one browser context, and a second run needs a fresh stack. The values to enter are the bundled Keycloak's
// (docs/local-development.md, "Trying the setup wizard").
//
// Secrets and the setup code: read in this process only, typed with typeSecret (no locator fill in a call log), never put in
// an assertion message. Traces, screenshots and videos are off (playwright.config.ts), and the line below keeps the page's
// aria snapshot, which could show a typed value, out of a failed test's error-context.md.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1'

const authority = 'https://localhost:7411/realms/fors'
const unreachableAuthority = 'https://localhost:1/realms/fors'
const wrongSecret = 'not-the-client-secret'
const password = () => appHostSecret('dev-user-password', 'E2E_PASSWORD')
const adminClientSecret = () => appHostSecret('oidc-client-secret', 'E2E_OIDC_CLIENT_SECRET')
const openWebUiClientSecret = () => appHostSecret('openwebui-client-secret', 'E2E_OPENWEBUI_CLIENT_SECRET')

/** `days` from today in Stockholm, as yyyy-mm-dd (the date field's value; Home counts the days in Stockholm too). */
const dateIn = (days: number) => new Date(Date.now() + days * 86_400_000).toLocaleDateString('sv-SE', { timeZone: 'Europe/Stockholm' })
const expiresOn = dateIn(10)

test.describe.serial('setup wizard', () => {
  let context: BrowserContext
  let page: Page
  const hook = (name: string) => page.getByTestId(name)
  const heading = () => hook('step-heading')
  const secretField = () => page.getByLabel('Client secret', { exact: true })

  /** Whether the element has the focus. */
  const focused = (locator: Locator) => locator.evaluate(element => element === document.activeElement)

  /**
   * Starts the test sign-in and signs in as `user` in its window at Keycloak; the window closes itself. Keycloak's session cookies
   * are cleared first, so its form always asks (the setup session's cookie, on /bff/setup, stays).
   */
  async function testSignIn(user: string): Promise<void> {
    await context.clearCookies({ name: /^(KEYCLOAK_|KC_|AUTH_SESSION_ID)/ })
    const opened = page.waitForEvent('popup')
    await page.getByRole('button', { name: 'Test sign-in' }).click()
    const popup = await opened
    await popup.waitForURL(/\/realms\/ume\//)
    await popup.locator('#username').fill(user)
    await typeSecret(popup.locator('#password'), password())
    const closed = popup.waitForEvent('close', { timeout: 60_000 })
    await popup.locator('#kc-login').click()
    await closed
    await expect(hook('test-pending')).toHaveCount(0, { timeout: 30_000 })
  }

  /** Finish on the summary step is disabled or enabled; back to the sign-in step afterwards. */
  async function expectFinish(enabled: boolean): Promise<void> {
    await hook('stepper-summary').click()
    await expect(heading()).toHaveText('Summary and finish')
    if (enabled) await expect(hook('finish')).not.toHaveAttribute('aria-disabled', 'true')
    else await expect(hook('finish')).toHaveAttribute('aria-disabled', 'true')
    await hook('stepper-signIn').click()
    await expect(heading()).toHaveText('Sign-in')
  }

  test.beforeAll(async ({ browser }) => {
    context = await browser.newContext()
    page = await context.newPage()
    const user = await (await page.request.get('/bff/user')).json()
    test.skip(user.setup?.open !== true,
      'Setup is not open on this stack (/bff/user setup.open is false): start a fresh one with scripts/Start-E2E.ps1 -Setup, then run --project=setup.')
  })

  test.afterAll(async () => { await context?.close() })

  test('before a session: the sign-in page offers setup, step 1 says where the code is and refuses a wrong one', async () => {
    await page.goto('/')
    await expect(hook('setup-open')).toBeVisible()
    await expect(page.getByRole('link', { name: 'Set up sign-in' })).toBeVisible()
    await expect(page.getByRole('link', { name: 'Sign in', exact: true })).toHaveCount(0)
    await scan(page, 'sign-in page while setup is open')

    await page.getByRole('link', { name: 'Set up sign-in' }).click()
    await page.waitForURL(url => url.pathname === '/setup')
    await expect(heading()).toHaveText('Setup code')
    // Open WebUI's step joins the count once the setup state says the deployment runs it.
    await expect(hook('step-of')).toHaveText(/^Step 1 of [45]$/)
    await expect(hook('code-step')).toContainText(/writes the code to its output/)
    await expect(hook('code-step')).toContainText('docker compose logs kalla-admin')
    await scan(page, 'step 1: setup code')

    // Well formed (26 Crockford base32 characters), but not the code. The throttle's 429 is covered by the unit tests.
    const code = page.getByRole('textbox', { name: 'Setup code' })
    await code.fill('00000-00000-00000-00000-000000')
    await page.getByRole('button', { name: 'Continue' }).click()
    await expect(hook('code-step')).toContainText(/The setup code is not right/)
    await expect(code).toBeFocused()
  })

  test('the code in lower case without dashes opens step 2: the redirect URIs copy, and a reload resumes past the code', async () => {
    let code: string | null = null
    await expect.poll(() => (code = setupCode()) !== null, { message: 'a setup code in kalla-admin’s output', timeout: 60_000 }).toBe(true)
    await typeSecret(page.getByRole('textbox', { name: 'Setup code' }), code!.toLowerCase().replaceAll('-', ''))
    await page.getByRole('button', { name: 'Continue' }).click()
    await expect(heading()).toHaveText('Addresses')
    await expect(heading()).toBeFocused()
    await expect(hook('step-of')).toHaveText('Step 2 of 5')

    const signIn = hook('redirect-sign-in').getByTestId('copy-value')
    const signOut = hook('redirect-sign-out').getByTestId('copy-value')
    await expect(signIn).toHaveText(/^https:\/\/\S+\/signin-oidc$/)
    await expect(signOut).toHaveText(/^https:\/\/\S+\/signout-callback-oidc$/)
    await context.grantPermissions(['clipboard-read', 'clipboard-write'])
    for (const [button, value] of [['Copy the redirect URI', signIn], ['Copy the post-logout redirect URI', signOut]] as const) {
      await page.getByRole('button', { name: button }).click()
      await expect.poll(() => page.evaluate(() => navigator.clipboard.readText()), { message: button }).toBe(await value.textContent())
    }
    await scan(page, 'step 2: addresses')

    await page.reload()
    await expect(heading()).toHaveText('Addresses')

    // Keyboard only: Tab reaches the stepper and then Next; after the step change the focus is on the new step's heading.
    let inStepper = false
    for (let presses = 0; presses < 40 && !inStepper; presses++) {
      await page.keyboard.press('Tab')
      inStepper = await page.evaluate(() => !!document.activeElement?.closest('[data-test="stepper"]'))
    }
    expect(inStepper, 'Tab reaches the stepper').toBe(true)
    let onNext = false
    for (let presses = 0; presses < 40 && !onNext; presses++) {
      await page.keyboard.press('Tab')
      onNext = await focused(hook('next'))
    }
    expect(onNext, 'Tab reaches Next').toBe(true)
    await page.keyboard.press('Enter')
    await expect(heading()).toHaveText('Sign-in')
    await expect(heading()).toBeFocused()
  })

  test('sign-in step: an unreachable provider and a wrong client secret fail with their messages, and Finish stays disabled', async () => {
    await expect(heading()).toHaveText('Sign-in')
    await expect(hook('step-of')).toHaveText('Step 3 of 5')
    await scan(page, 'step 3: sign-in')

    const provider = page.getByRole('textbox', { name: 'Provider address' })
    await provider.fill(unreachableAuthority)
    await typeSecret(secretField(), wrongSecret)
    await page.getByRole('button', { name: 'Test sign-in' }).click()
    await expect(hook('test-error')).toContainText(/could not reach the provider address/)

    await provider.fill(authority)
    await testSignIn('rag-platform-admin')
    await expect(hook('test-failed')).toContainText(/Most often the client secret or the client ID is wrong/)
    await scan(page, 'step 3: a failed test')
    await expectFinish(false)
  })

  test('a person who is not a platform admin passes the test, but Finish stays disabled', async () => {
    await typeSecret(secretField(), adminClientSecret())
    await testSignIn('rag-viewer')
    await expect(hook('test-passed')).toBeVisible()
    await expect(hook('is-admin')).toHaveText('No')
    await expect(hook('not-admin-help')).toContainText(/Give this person the role rag-platform-admin/)
    await expectFinish(false)
  })

  test('a platform admin passes; a changed value makes the test stale until it is changed back', async () => {
    const expiry = page.getByLabel('Secret expires on (optional)')
    await expiry.fill(expiresOn)
    await testSignIn('rag-platform-admin')
    await expect(hook('test-passed')).toBeVisible()
    await expect(hook('is-admin')).toHaveText('Yes')
    await expect(hook('test-stale')).toHaveCount(0)
    await scan(page, 'step 3: a passed test')

    await expiry.fill(dateIn(11))
    await expect(hook('test-stale')).toContainText(/Test again before finishing/)
    await expectFinish(false)
    await expiry.fill(expiresOn)
    await expect(hook('test-stale')).toHaveCount(0)
    await expectFinish(true)
  })

  test('Open WebUI step: a wrong secret is refused and cannot be saved, the right one is accepted and saved', async () => {
    await hook('stepper-openWebUi').click()
    await expect(heading()).toHaveText('Open WebUI')
    await expect(hook('step-of')).toHaveText('Step 4 of 5')
    await expect(hook('openwebui-redirect').getByTestId('copy-value')).toHaveText('http://localhost:7410/oauth/oidc/login/callback')
    await expect(hook('next')).toContainText('Skip for now')
    await expect(hook('admin-test-first')).toHaveCount(0)
    await scan(page, 'step 4: Open WebUI')

    // The provider address stays empty: it follows the sign-in provider of the passed test.
    await expect(page.getByRole('textbox', { name: /^Provider address/ })).toHaveValue('')
    await page.getByRole('textbox', { name: 'Client ID' }).fill('open-webui-dev')
    await typeSecret(secretField(), wrongSecret)
    await hook('openwebui-check').click()
    await expect(hook('openwebui-client-refused')).toContainText('Refused')
    await expect(hook('openwebui-save')).toHaveAttribute('aria-disabled', 'true')

    await typeSecret(secretField(), openWebUiClientSecret())
    await hook('openwebui-check').click()
    await expect(hook('openwebui-client-accepted')).toContainText('Accepted')
    await expect(hook('openwebui-save')).not.toHaveAttribute('aria-disabled', 'true')
    await hook('openwebui-save').click()
    await expect(hook('openwebui-saved')).toContainText(/the provider accepted the client/)
    await expect(hook('next')).toContainText('Continue')
    await scan(page, 'step 4: Open WebUI saved')
  })

  test('summary: the values with their sources, Open WebUI saved in this session, the tested person', async () => {
    await hook('next').click()
    await expect(heading()).toHaveText('Summary and finish')
    await expect(heading()).toBeFocused()
    await expect(hook('step-of')).toHaveText('Step 5 of 5')
    await expect(hook('summary-authority')).toContainText(authority)
    await expect(hook('summary-authority')).toContainText('Set here')
    await expect(hook('summary-clientId')).toContainText('kalla-admin')
    await expect(hook('summary-secret')).toHaveText('The secret you typed for the test')
    await expect(hook('summary-values')).toContainText(expiresOn)
    await expect(hook('summary-openwebui-outcome')).toHaveText('Saved in this session')
    await expect(hook('summary-openwebui-clientId')).toContainText('open-webui-dev')
    await expect(hook('summary-openwebui-clientId')).toContainText('Set here')
    await expect(hook('summary-openwebui-secret')).toHaveText('A secret is stored')
    await expect(hook('summary-person')).not.toHaveText('Not tested yet')
    await expect(hook('summary-person')).toHaveText(/\S+ \(\S+\)/)
    await expect(hook('finish')).not.toHaveAttribute('aria-disabled', 'true')
    await scan(page, 'step 5: summary')
  })

  test('Finish closes setup; a platform admin then signs in, and the settings show the wizard’s values without the secrets', async () => {
    await hook('finish').click()
    await expect(page.locator('#setup-done-heading')).toHaveText('Sign-in is set up')
    await expect(page.locator('#setup-done-heading')).toBeFocused()
    await expect(hook('done-sign-in')).toBeVisible()
    await scan(page, 'done')
    expect((await page.request.get('/bff/setup/state')).status(), 'GET /bff/setup/state after Finish').toBe(404)

    await page.goto('/setup')
    await expect(hook('setup-closed')).toContainText('Setup is closed')
    await hook('setup-closed').getByRole('link', { name: 'Go to sign-in' }).click()
    await expect(page.getByRole('link', { name: 'Set up sign-in' })).toHaveCount(0)
    await context.clearCookies({ name: /^(KEYCLOAK_|KC_|AUTH_SESSION_ID)/ })
    await page.getByRole('link', { name: 'Sign in', exact: true }).click()
    await page.locator('#username').fill('rag-platform-admin')
    await typeSecret(page.locator('#password'), password())
    await page.locator('#kc-login').click()
    await expect(page.getByRole('navigation', { name: 'Main menu' })).toBeVisible()
    const user = await (await page.request.get('/bff/user')).json()
    expect(user.isAuthenticated).toBe(true)
    expect(user.roles).toContain('rag-platform-admin')

    const response = await page.request.get('/api/settings/sign-in')
    expect(response.status()).toBe(200)
    const raw = await response.text()
    expect(raw.includes(adminClientSecret()), 'GET /api/settings/sign-in holds kalla-admin’s client secret').toBe(false)
    expect(raw.includes(openWebUiClientSecret()), 'GET /api/settings/sign-in holds Open WebUI’s client secret').toBe(false)
    const settings = JSON.parse(raw)
    expect(settings.authority).toEqual({ value: authority, source: 'wizard' })
    expect(settings.clientSecret).toMatchObject({ set: true, source: 'wizard', expiresOn })
    expect(settings.openWebUi.signIn.clientId).toMatchObject({ value: 'open-webui-dev', source: 'wizard' })
    expect(settings.openWebUi.signIn.clientSecret).toMatchObject({ set: true, source: 'wizard' })
  })

  test('Home reminds of the admin UI’s secret expiry, and Hide survives a reload', async () => {
    await ready(page, '/')
    const notice = hook('secret-expiry-rag-admin')
    await expect(notice).toContainText(/The sign-in client secret of the admin UI expires on .+ \(in \d+ days\)/)
    await notice.getByRole('button', { name: 'Hide the reminder for the admin UI' }).click()
    await expect(notice).toHaveCount(0)
    await ready(page)
    await expect(notice).toHaveCount(0)
  })
})

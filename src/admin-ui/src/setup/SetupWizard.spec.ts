import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import type { SetupState } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser, setupState, setupTest, signInSettings, wizardValues } from '@/test/fixtures'
import { SETUP_WIZARD } from '@/utils/labels'
import { ERROR_MESSAGES, SETUP_COMPLETE_MESSAGES, SETUP_TEST_MESSAGES } from '@/utils/messages'
import SetupWizard from './SetupWizard.vue'

const L = SETUP_WIZARD
const AUTHORITY = 'https://idp.example.org/realms/ume'
const AUTHORIZE = 'https://idp.example.org/realms/ume/protocol/openid-connect/auth?state=rag-setup-test.x'

const noSession = () => new ApiError(401, 'GET', { code: 'setup_session_required' })
const fakePopup = () => ({ closed: false, close: vi.fn(), location: { href: 'about:blank' } })

async function mountWizard(open = true): Promise<VueWrapper> {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ isAuthenticated: false, name: null, signIn: { configured: false }, setup: { open } })
  auth.status = 'unauthenticated'
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: {} }, { path: '/setup', component: {} }] })
  const wrapper = mount(SetupWizard, { attachTo: document.body, global: { plugins: [router] } })
  await flushPromises()
  return wrapper
}

const heading = (wrapper: VueWrapper) => wrapper.get('[data-test=step-heading]').text()

async function enterCode(wrapper: VueWrapper, code: string) {
  await wrapper.get('#setup-code').setValue(code)
  await wrapper.get('[data-test=code-step]').trigger('submit')
  await flushPromises()
}

async function goTo(wrapper: VueWrapper, step: 'addresses' | 'signIn' | 'summary') {
  await wrapper.get(`[data-test=stepper-${step}]`).trigger('click')
  await flushPromises()
}

async function startTest(wrapper: VueWrapper) {
  await wrapper.get('[data-test=sign-in-step]').trigger('submit')
  await flushPromises()
}

/** A session that resumes with `state` (a reload with a live setup cookie). */
async function resume(state: SetupState): Promise<VueWrapper> {
  vi.spyOn(api.setup, 'state').mockResolvedValue(state)
  return mountWizard()
}

describe('setup wizard: the setup code (admin design §5.11 step 1)', () => {
  it('starts at the code without a setup session, and says where to find it', async () => {
    vi.spyOn(api.setup, 'state').mockRejectedValue(noSession())
    const wrapper = await mountWizard()
    expect(heading(wrapper)).toBe(L.steps.code)
    expect(wrapper.find('[data-test=setup-notice]').exists()).toBe(false)
    expect(wrapper.text()).toContain('docker compose logs kalla-admin')
    expect(wrapper.text()).toContain('kubectl logs deploy/kalla-admin')
    wrapper.unmount()
  })

  it('sends the code as typed (trimmed only) and goes on to the addresses', async () => {
    const state = vi.spyOn(api.setup, 'state').mockRejectedValueOnce(noSession()).mockResolvedValue(setupState())
    const session = vi.spyOn(api.setup, 'session').mockResolvedValue({ expiresAt: '', setupEndsAt: '' })
    const wrapper = await mountWizard()
    await enterCode(wrapper, '  abcde-FGHIJ-klmno ')
    expect(session).toHaveBeenCalledWith('abcde-FGHIJ-klmno')
    expect(state).toHaveBeenCalledTimes(2)
    expect(heading(wrapper)).toBe(L.steps.addresses)
    expect(wrapper.get('[data-test=redirect-sign-in]').text()).toContain('https://kalla-admin.example.org/signin-oidc')
    expect(wrapper.get('[data-test=window-ends]').text()).toContain('(in 50 min)')
    wrapper.unmount()
  })

  it('says a wrong code is wrong and stays', async () => {
    vi.spyOn(api.setup, 'state').mockRejectedValue(noSession())
    vi.spyOn(api.setup, 'session').mockRejectedValue(new ApiError(400, 'POST', { code: 'setup_code_invalid' }))
    const wrapper = await mountWizard()
    await enterCode(wrapper, 'wrong')
    expect(wrapper.text()).toContain(ERROR_MESSAGES.setup_code_invalid)
    expect(heading(wrapper)).toBe(L.steps.code)
    wrapper.unmount()
  })

  describe('with the attempt limit', () => {
    beforeEach(() => vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] }))
    afterEach(() => vi.useRealTimers())

    it('counts down Retry-After with the button disabled', async () => {
      vi.spyOn(api.setup, 'state').mockRejectedValue(noSession())
      vi.spyOn(api.setup, 'session').mockRejectedValue(new ApiError(429, 'POST', { code: 'setup_attempts_exhausted', retryAfter: 3 }))
      const wrapper = await mountWizard()
      await enterCode(wrapper, 'abc')
      expect(wrapper.get('[data-test=code-wait]').text()).toBe(L.code.wait(3))
      expect(wrapper.get('[data-test=code-submit]').attributes('aria-disabled')).toBe('true')
      vi.advanceTimersByTime(1000)
      await flushPromises()
      expect(wrapper.get('[data-test=code-wait]').text()).toBe(L.code.wait(2))
      vi.advanceTimersByTime(2000)
      await flushPromises()
      expect(wrapper.find('[data-test=code-wait]').exists()).toBe(false)
      expect(wrapper.get('[data-test=code-submit]').attributes('aria-disabled')).toBeUndefined()
      wrapper.unmount()
    })
  })
})

describe('setup wizard: closed setup', () => {
  it('says setup is closed, without a call, when /bff/user says so', async () => {
    const state = vi.spyOn(api.setup, 'state')
    const wrapper = await mountWizard(false)
    expect(state).not.toHaveBeenCalled()
    expect(wrapper.get('[data-test=setup-closed]').text()).toContain(L.closed.title)
    expect(wrapper.get('[data-test=setup-closed] a').attributes('href')).toBe('/')
    wrapper.unmount()
  })

  it('says setup is closed when the API answers 404', async () => {
    vi.spyOn(api.setup, 'state').mockRejectedValue(new ApiError(404, 'GET', null))
    const wrapper = await mountWizard()
    expect(wrapper.get('[data-test=setup-closed]').text()).toContain(L.closed.title)
    wrapper.unmount()
  })
})

describe('setup wizard: sign-in and the test (admin design §5.11 step 3)', () => {
  it('resumes a live session at the sign-in with the latest test shown', async () => {
    const wrapper = await resume(setupState({ latestTest: setupTest() }))
    expect(heading(wrapper)).toBe(L.steps.signIn)
    expect((wrapper.get('#setup-authority').element as HTMLInputElement).value).toBe(AUTHORITY)
    expect(wrapper.find('[data-test=test-passed]').exists()).toBe(true)
    expect(wrapper.get('[data-test=is-admin]').text()).toBe('Yes')
    expect(wrapper.get('[data-test=claims-read]').text()).toBe(L.result.claimRead('roles', 'groups'))
    wrapper.unmount()
  })

  it('locks a value the deployment sets and sends null for it', async () => {
    const state = setupState({ signIn: signInSettings({ authority: { value: AUTHORITY, source: 'deployment' } }) })
    const wrapper = await resume(state)
    await goTo(wrapper, 'signIn')
    const authority = wrapper.get('#setup-authority')
    expect((authority.element as HTMLInputElement).disabled).toBe(true)
    expect((authority.element as HTMLInputElement).value).toBe(AUTHORITY)
    expect(wrapper.get('#setup-authority-hint').text()).toBe(L.signIn.locked)
    vi.spyOn(window, 'open').mockReturnValue(fakePopup() as unknown as Window)
    const test = vi.spyOn(api.setup, 'test').mockResolvedValue({ testId: 't2', authorizeUrl: AUTHORIZE, expiresAt: '' })
    await startTest(wrapper)
    expect(test).toHaveBeenCalledWith(expect.objectContaining({ authority: null, clientSecret: null }))
    wrapper.unmount()
  })

  it('sends a typed secret, or "" to remove it, and opens the provider in the window it opened on the click', async () => {
    const stored = signInSettings({ clientSecret: { set: true, source: 'wizard', setAt: null, expiresOn: null, unreadable: false } })
    const wrapper = await resume(setupState({ signIn: stored }))
    await goTo(wrapper, 'signIn')
    expect(wrapper.get('#setup-clientSecret-hint').text()).toBe(L.signIn.secretStored)
    const popup = fakePopup()
    const open = vi.spyOn(window, 'open').mockReturnValue(popup as unknown as Window)
    const test = vi.spyOn(api.setup, 'test').mockResolvedValue({ testId: 't2', authorizeUrl: AUTHORIZE, expiresAt: '' })
    await wrapper.get('#setup-authority').setValue(AUTHORITY)
    await wrapper.get('#setup-clientSecret').setValue('s3cret')
    await startTest(wrapper)
    expect(open).toHaveBeenCalledWith('about:blank', 'kalla-setup-test', expect.any(String))
    expect(test).toHaveBeenLastCalledWith(expect.objectContaining({ authority: AUTHORITY, clientSecret: 's3cret' }))
    expect(popup.location.href).toBe(AUTHORIZE)

    await wrapper.get('[data-test=remove-secret] input').setValue(true)
    await startTest(wrapper)
    expect(test).toHaveBeenLastCalledWith(expect.objectContaining({ clientSecret: '' }))
    wrapper.unmount()
  })

  it('needs a provider address, and a tenant id for Entra ID, before it tests', async () => {
    const wrapper = await resume(setupState())
    await goTo(wrapper, 'signIn')
    const test = vi.spyOn(api.setup, 'test')
    await startTest(wrapper)
    expect(wrapper.text()).toContain(L.signIn.authorityRequired)
    await wrapper.get('#setup-authority').setValue(AUTHORITY)
    await wrapper.get('[data-test=preset] [role=radio]:last-child').trigger('click')
    await startTest(wrapper)
    expect(wrapper.text()).toContain(L.signIn.tenantRequired)
    expect(test).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('shows a link when the browser blocks the window', async () => {
    const state = vi.spyOn(api.setup, 'state').mockResolvedValue(setupState())
    const wrapper = await mountWizard()
    await goTo(wrapper, 'signIn')
    vi.spyOn(window, 'open').mockReturnValue(null)
    vi.spyOn(api.setup, 'test').mockResolvedValue({ testId: 't2', authorizeUrl: AUTHORIZE, expiresAt: '' })
    state.mockResolvedValue(setupState({ latestTest: setupTest({ testId: 't2', status: 'pending', result: null }) }))
    await wrapper.get('#setup-authority').setValue(AUTHORITY)
    await startTest(wrapper)
    expect(wrapper.find('[data-test=test-pending]').exists()).toBe(true)
    expect(wrapper.get('[data-test=popup-blocked] a').attributes('href')).toBe(AUTHORIZE)
    wrapper.unmount()
  })

  it('says why a test could not start', async () => {
    const wrapper = await resume(setupState())
    await goTo(wrapper, 'signIn')
    const popup = fakePopup()
    vi.spyOn(window, 'open').mockReturnValue(popup as unknown as Window)
    vi.spyOn(api.setup, 'test').mockRejectedValue(new ApiError(400, 'POST', { code: 'setup_test_refused', params: { reason: 'secret_required' } }))
    await wrapper.get('#setup-authority').setValue(AUTHORITY)
    await startTest(wrapper)
    expect(wrapper.get('[data-test=test-error]').text()).toContain(SETUP_TEST_MESSAGES.secret_required)
    expect(popup.close).toHaveBeenCalled()
    wrapper.unmount()
  })

  it('says why a test failed at the callback, and what to do for a person who is not a platform admin', async () => {
    const failed = setupTest({ status: 'failed', reason: 'token_refused', result: null })
    let wrapper = await resume(setupState({ latestTest: failed }))
    expect(wrapper.get('[data-test=test-failed]').text()).toContain(SETUP_TEST_MESSAGES.token_refused)
    wrapper.unmount()

    const base = setupTest()
    const notAdmin = setupTest({ result: { ...base.result!, isPlatformAdmin: false, roles: [], groups: ['a', 'b'], groupCount: 140 } })
    wrapper = await resume(setupState({ latestTest: notAdmin, deployment: { gatewayBaseUrl: null, apiPublicUrl: null, platformAdminGroups: ['rag-admins'] } }))
    expect(wrapper.get('[data-test=is-admin]').text()).toBe('No')
    expect(wrapper.get('[data-test=groups-capped]').text()).toContain(L.result.groupsCapped(2, 140))
    const help = wrapper.get('[data-test=not-admin-help]').text()
    expect(help).toContain(L.result.notAdminHelp('roles'))
    expect(help).toContain('rag-admins')
    wrapper.unmount()
  })

  it('marks a passed test stale when a value changes after it', async () => {
    const wrapper = await resume(setupState({ latestTest: setupTest() }))
    expect(wrapper.find('[data-test=test-stale]').exists()).toBe(false)
    await wrapper.get('#setup-clientId').setValue('another-client')
    expect(wrapper.find('[data-test=test-stale]').exists()).toBe(true)
    await goTo(wrapper, 'summary')
    expect(wrapper.get('[data-test=finish]').attributes('aria-disabled')).toBe('true')
    expect(wrapper.text()).toContain(L.summary.finishNeeds)
    wrapper.unmount()
  })

  describe('while a test waits', () => {
    beforeEach(() => vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] }))
    afterEach(() => vi.useRealTimers())

    it('reads the state every 2 seconds until the result is there', async () => {
      const pending = setupTest({ status: 'pending', result: null })
      const state = vi.spyOn(api.setup, 'state').mockResolvedValue(setupState({ latestTest: pending }))
      const wrapper = await mountWizard()
      expect(wrapper.get('[data-test=test-pending]').text()).toContain(L.signIn.waiting)
      state.mockResolvedValue(setupState({ latestTest: setupTest() }))
      vi.advanceTimersByTime(2000)
      await flushPromises()
      expect(wrapper.find('[data-test=test-passed]').exists()).toBe(true)
      const calls = state.mock.calls.length
      vi.advanceTimersByTime(4000)
      await flushPromises()
      expect(state.mock.calls.length).toBe(calls)
      wrapper.unmount()
    })

    it('lets the person cancel and start over', async () => {
      vi.spyOn(api.setup, 'state').mockResolvedValue(setupState({ latestTest: setupTest({ status: 'pending', result: null }) }))
      const wrapper = await mountWizard()
      await wrapper.get('[data-test=test-cancel]').trigger('click')
      expect(wrapper.find('[data-test=test-pending]').exists()).toBe(false)
      expect(wrapper.get('[data-test=test]').attributes('aria-disabled')).toBeUndefined()
      wrapper.unmount()
    })
  })
})

describe('setup wizard: the provider address from the gateway (identity design §19)', () => {
  const GATEWAY_AUTHORITY = 'https://login.example.org/realms/kommun'
  const authorityInput = (wrapper: VueWrapper) => wrapper.get('#setup-authority').element as HTMLInputElement

  it('fills in the gateway\'s address when nothing sets one, says where it came from, and tests it like a typed one', async () => {
    const wrapper = await resume(setupState({ gateway: { suggestedAuthority: GATEWAY_AUTHORITY, adminUrl: 'https://llm-admin.example.org' } }))
    await goTo(wrapper, 'signIn')
    expect(authorityInput(wrapper).value).toBe(GATEWAY_AUTHORITY)
    expect(authorityInput(wrapper).disabled).toBe(false)
    expect(wrapper.get('#setup-authority-hint').text()).toBe(L.signIn.authorityFromGateway)
    expect(wrapper.find('[data-test=gateway-sign-in-missing]').exists()).toBe(false)

    vi.spyOn(window, 'open').mockReturnValue(fakePopup() as unknown as Window)
    const test = vi.spyOn(api.setup, 'test').mockResolvedValue({ testId: 't2', authorizeUrl: AUTHORIZE, expiresAt: '' })
    await startTest(wrapper)
    expect(wrapper.text()).not.toContain(L.signIn.authorityRequired)
    expect(test).toHaveBeenCalledWith(expect.objectContaining({ authority: GATEWAY_AUTHORITY }))

    // Changed by the person: their own value, with the field's usual hint.
    await wrapper.get('#setup-authority').setValue(AUTHORITY)
    expect(wrapper.get('#setup-authority-hint').text()).toBe(L.signIn.authorityHint)
    wrapper.unmount()
  })

  it('keeps the wizard\'s own value and a deployment\'s value instead of the gateway\'s', async () => {
    const gateway = { suggestedAuthority: GATEWAY_AUTHORITY, adminUrl: null }
    let wrapper = await resume(setupState({ gateway, signIn: signInSettings({ authority: { value: AUTHORITY, source: 'wizard' } }) }))
    await goTo(wrapper, 'signIn')
    expect(authorityInput(wrapper).value).toBe(AUTHORITY)
    expect(wrapper.get('#setup-authority-hint').text()).toBe(L.signIn.authorityHint)
    wrapper.unmount()

    wrapper = await resume(setupState({ gateway, signIn: signInSettings({ authority: { value: AUTHORITY, source: 'deployment' } }) }))
    await goTo(wrapper, 'signIn')
    expect(authorityInput(wrapper).value).toBe(AUTHORITY)
    expect(authorityInput(wrapper).disabled).toBe(true)
    expect(wrapper.get('#setup-authority-hint').text()).toBe(L.signIn.locked)
    wrapper.unmount()
  })

  it('says the gateway\'s sign-in is not set up yet, with a link to its setup when its admin address is known', async () => {
    let wrapper = await resume(setupState({ gateway: { suggestedAuthority: null, adminUrl: 'https://llm-admin.example.org/' } }))
    await goTo(wrapper, 'signIn')
    expect(authorityInput(wrapper).value).toBe('')
    expect(wrapper.get('[data-test=gateway-sign-in-missing]').text()).toContain(L.signIn.gatewayWithoutAuthority)
    const link = wrapper.get('[data-test=gateway-setup-link]')
    expect(link.attributes('href')).toBe('https://llm-admin.example.org/setup')
    expect(link.attributes('target')).toBe('_blank')
    expect(link.attributes('rel')).toBe('noopener noreferrer')
    wrapper.unmount()

    wrapper = await resume(setupState({ gateway: { suggestedAuthority: null, adminUrl: null } }))
    await goTo(wrapper, 'signIn')
    expect(wrapper.find('[data-test=gateway-sign-in-missing]').exists()).toBe(true)
    expect(wrapper.find('[data-test=gateway-setup-link]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('reads the state again when the tab is shown again, and fills in an address the gateway has by then', async () => {
    const adminUrl = 'https://llm-admin.example.org'
    const state = vi.spyOn(api.setup, 'state').mockResolvedValue(setupState({ gateway: { suggestedAuthority: null, adminUrl } }))
    const wrapper = await mountWizard()
    await goTo(wrapper, 'signIn')
    expect(wrapper.find('[data-test=gateway-sign-in-missing]').exists()).toBe(true)

    // The person finishes the gateway's wizard in the other tab and comes back.
    state.mockResolvedValue(setupState({ gateway: { suggestedAuthority: GATEWAY_AUTHORITY, adminUrl } }))
    const calls = state.mock.calls.length
    document.dispatchEvent(new Event('visibilitychange'))
    await flushPromises()
    expect(state.mock.calls.length).toBe(calls + 1)
    expect(authorityInput(wrapper).value).toBe(GATEWAY_AUTHORITY)
    expect(wrapper.get('#setup-authority-hint').text()).toBe(L.signIn.authorityFromGateway)
    expect(wrapper.find('[data-test=gateway-sign-in-missing]').exists()).toBe(false)

    // A typed address is the person's own: a later read leaves it.
    await wrapper.get('#setup-authority').setValue(AUTHORITY)
    document.dispatchEvent(new Event('visibilitychange'))
    await flushPromises()
    expect(authorityInput(wrapper).value).toBe(AUTHORITY)
    wrapper.unmount()
  })

  it('changes nothing for a gateway without the feature', async () => {
    const wrapper = await resume(setupState({ gateway: null }))
    await goTo(wrapper, 'signIn')
    expect(authorityInput(wrapper).value).toBe('')
    expect(wrapper.get('#setup-authority-hint').text()).toBe(L.signIn.authorityHint)
    expect(wrapper.find('[data-test=gateway-sign-in-missing]').exists()).toBe(false)
    wrapper.unmount()
  })
})

describe('setup wizard: summary and finish (admin design §5.11 step 5)', () => {
  it('finishes with the tested values and the test id, then offers the sign-in', async () => {
    const tested = setupTest({ values: wizardValues({ authority: AUTHORITY, tenantId: 'tid' }) })
    const wrapper = await resume(setupState({ latestTest: tested }))
    await goTo(wrapper, 'summary')
    expect(wrapper.get('[data-test=summary-authority]').text()).toContain(L.summary.sources.wizard)
    expect(wrapper.get('[data-test=summary-clientId]').text()).toContain('kalla-admin')
    expect(wrapper.get('[data-test=summary-clientId]').text()).toContain(L.summary.sources.default)
    expect(wrapper.get('[data-test=summary-secret]').text()).toBe(L.summary.secret.typed)
    expect(wrapper.get('[data-test=summary-person]').text()).toContain('Anna Andersson')
    const complete = vi.spyOn(api.setup, 'complete').mockResolvedValue({ signInUrl: '/bff/login' })
    await wrapper.get('[data-test=finish]').trigger('click')
    await flushPromises()
    expect(complete).toHaveBeenCalledWith({ ...wizardValues({ authority: AUTHORITY, tenantId: 'tid' }), testId: 't1' })
    expect(wrapper.get('[data-test=setup-done]').text()).toContain(L.summary.done)
    expect(wrapper.get('[data-test=done-sign-in]').attributes('href')).toBe('/bff/login')
    wrapper.unmount()
  })

  it('does nothing on Finish without a passed test by a platform admin', async () => {
    const wrapper = await resume(setupState())
    await goTo(wrapper, 'summary')
    const complete = vi.spyOn(api.setup, 'complete')
    expect(wrapper.get('[data-test=finish]').attributes('aria-disabled')).toBe('true')
    await wrapper.get('[data-test=finish]').trigger('click')
    expect(complete).not.toHaveBeenCalled()
    expect(wrapper.get('[data-test=summary-person]').text()).toBe(L.summary.notTested)
    wrapper.unmount()
  })

  it('says why a completion was refused', async () => {
    const wrapper = await resume(setupState({ latestTest: setupTest() }))
    await goTo(wrapper, 'summary')
    vi.spyOn(api.setup, 'complete').mockRejectedValue(new ApiError(409, 'POST', { code: 'setup_test_required', params: { reason: 'not_latest' } }))
    await wrapper.get('[data-test=finish]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-test=finish-error]').text()).toContain(SETUP_COMPLETE_MESSAGES.not_latest)
    wrapper.unmount()
  })

  it('shows that setup has closed when Finish answers 404', async () => {
    const wrapper = await resume(setupState({ latestTest: setupTest() }))
    await goTo(wrapper, 'summary')
    vi.spyOn(api.setup, 'complete').mockRejectedValue(new ApiError(404, 'POST', null))
    await wrapper.get('[data-test=finish]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-test=setup-closed]').text()).toContain(L.closed.closedMeanwhile)
    wrapper.unmount()
  })
})

describe('setup wizard: the session ends', () => {
  it('goes back to the code on a 401, keeps the typed values and drops the secret', async () => {
    const state = vi.spyOn(api.setup, 'state').mockResolvedValue(setupState())
    vi.spyOn(api.setup, 'session').mockResolvedValue({ expiresAt: '', setupEndsAt: '' })
    const wrapper = await mountWizard()
    await goTo(wrapper, 'signIn')
    await wrapper.get('#setup-authority').setValue(AUTHORITY)
    await wrapper.get('#setup-clientSecret').setValue('s3cret')
    vi.spyOn(window, 'open').mockReturnValue(fakePopup() as unknown as Window)
    vi.spyOn(api.setup, 'test').mockRejectedValue(new ApiError(401, 'POST', { code: 'setup_session_required' }))
    await startTest(wrapper)
    expect(heading(wrapper)).toBe(L.steps.code)
    expect(wrapper.get('[data-test=setup-notice]').text()).toContain(L.sessionEnded)

    state.mockResolvedValue(setupState())
    await enterCode(wrapper, 'abc')
    await goTo(wrapper, 'signIn')
    expect((wrapper.get('#setup-authority').element as HTMLInputElement).value).toBe(AUTHORITY)
    expect((wrapper.get('#setup-clientSecret').element as HTMLInputElement).value).toBe('')
    wrapper.unmount()
  })

  describe('in time', () => {
    beforeEach(() => vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] }))
    afterEach(() => vi.useRealTimers())

    it('goes back to the code when the session expires, and names the window when that ends', async () => {
      const now = Date.now()
      const state = vi.spyOn(api.setup, 'state').mockResolvedValue(setupState({}, now))
      vi.spyOn(api.setup, 'session').mockResolvedValue({ expiresAt: '', setupEndsAt: '' })
      const wrapper = await mountWizard()
      expect(heading(wrapper)).toBe(L.steps.addresses)
      vi.advanceTimersByTime(25 * 60_000)
      await flushPromises()
      expect(heading(wrapper)).toBe(L.steps.code)
      expect(wrapper.get('[data-test=setup-notice]').text()).toContain(L.sessionEnded)

      // A new session that outlasts the window (the window still ends 50 minutes after the start).
      state.mockResolvedValue(setupState({ sessionExpiresAt: new Date(now + 80 * 60_000).toISOString() }, now))
      await enterCode(wrapper, 'abc')
      expect(heading(wrapper)).toBe(L.steps.addresses)
      vi.advanceTimersByTime(25 * 60_000)
      await flushPromises()
      expect(wrapper.get('[data-test=setup-notice]').text()).toContain(L.windowEnded)
      wrapper.unmount()
    })
  })
})

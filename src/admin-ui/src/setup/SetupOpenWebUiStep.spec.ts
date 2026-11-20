import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import type { OpenWebUiSaved, SetupOpenWebUiState, SetupState } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser, openWebUiSignIn, openWebUiState, openWebUiTest, openWebUiValues, setupState, setupTest } from '@/test/fixtures'
import { SETUP_WIZARD } from '@/utils/labels'
import { OPENWEBUI_CHECK_MESSAGES, SETUP_COMPLETE_MESSAGES, SETUP_TEST_MESSAGES } from '@/utils/messages'
import SetupWizard from './SetupWizard.vue'

const L = SETUP_WIZARD
const W = SETUP_WIZARD.openWebUi
const DISCOVERY = 'https://idp.example.org/realms/ume/.well-known/openid-configuration'
const CHAT_IDP = 'https://chat-idp.example.org/realms/chat/.well-known/openid-configuration'

/** The wizard resumed (a live setup session) with `state`. */
async function resume(state: SetupState): Promise<VueWrapper> {
  vi.spyOn(api.setup, 'state').mockResolvedValue(state)
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ isAuthenticated: false, name: null, signIn: { configured: false }, setup: { open: true } })
  auth.status = 'unauthenticated'
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: {} }, { path: '/setup', component: {} }] })
  const wrapper = mount(SetupWizard, { attachTo: document.body, global: { plugins: [router] } })
  await flushPromises()
  return wrapper
}

/** A session whose kalla-admin test passed as a platform admin, with Open WebUI run by the deployment and `openWebUi` over that. */
function withChat(openWebUi: Partial<SetupOpenWebUiState> = {}, adminTest = true): SetupState {
  return setupState({ latestTest: adminTest ? setupTest() : null, openWebUi: openWebUiState({ enabled: true, ...openWebUi }) })
}

const heading = (wrapper: VueWrapper) => wrapper.get('[data-test=step-heading]').text()
const input = (wrapper: VueWrapper, id: string) => wrapper.get(`#${id}`).element as HTMLInputElement
const saveDisabled = (wrapper: VueWrapper) => wrapper.get('[data-test=openwebui-save]').attributes('aria-disabled') === 'true'

async function step(wrapper: VueWrapper, key: 'signIn' | 'openWebUi' | 'summary') {
  await wrapper.get(`[data-test=stepper-${key}]`).trigger('click')
  await flushPromises()
}

async function check(wrapper: VueWrapper) {
  await wrapper.get('[data-test=openwebui-step]').trigger('submit')
  await flushPromises()
}

const saved = (clientVerified: boolean): OpenWebUiSaved => ({ openWebUi: { enabled: true, signIn: openWebUiSignIn(), redirectUri: null }, clientVerified })

describe('setup wizard: the Open WebUI step (admin design §5.11 step 4)', () => {
  it('is a step only when the deployment runs Open WebUI', async () => {
    let wrapper = await resume(setupState())
    expect(wrapper.find('[data-test=stepper-openWebUi]').exists()).toBe(false)
    expect(wrapper.get('[data-test=step-of]').text()).toBe(L.stepOf(2, 4))
    wrapper.unmount()

    wrapper = await resume(withChat({}, false))
    expect(wrapper.findAll('[data-test=stepper] button').map((b) => b.attributes('data-test'))).toEqual([
      'stepper-code', 'stepper-addresses', 'stepper-signIn', 'stepper-openWebUi', 'stepper-summary',
    ])
    expect(wrapper.get('[data-test=stepper-openWebUi]').text()).toContain(L.steps.openWebUi)
    expect(wrapper.get('[data-test=step-of]').text()).toBe(L.stepOf(2, 5))
    await step(wrapper, 'openWebUi')
    expect(heading(wrapper)).toBe(L.steps.openWebUi)
    expect(wrapper.get('[data-test=step-of]').text()).toBe(L.stepOf(4, 5))
    expect(wrapper.get('[data-test=openwebui-redirect]').text()).toContain('https://chat.example.org/oauth/oidc/login/callback')
    wrapper.unmount()
  })

  it('says when kalla-admin does not know the chat\'s address, and shows the default provider address as following the sign-in provider', async () => {
    const wrapper = await resume(withChat({ redirectUri: null }))
    await step(wrapper, 'openWebUi')
    expect(wrapper.get('[data-test=openwebui-no-redirect]').text()).toBe(W.noRedirectUri)
    expect(input(wrapper, 'setup-owui-providerUrl').value).toBe('')
    expect(input(wrapper, 'setup-owui-providerUrl').placeholder).toBe(DISCOVERY)
    expect(wrapper.get('#setup-owui-providerUrl-hint').text()).toBe(W.followsSignIn(DISCOVERY))
    expect(wrapper.get('#setup-owui-clientId-hint').text()).toBe(L.signIn.defaultHint('open-webui'))
    wrapper.unmount()
  })

  it('checks with this target\'s members only, null for what is left empty, and the secret in three states', async () => {
    const stored = openWebUiSignIn({ clientSecret: { set: true, source: 'wizard', setAt: null, expiresOn: null, unreadable: false } })
    const wrapper = await resume(withChat({ signIn: stored }))
    await step(wrapper, 'openWebUi')
    expect(wrapper.get('#setup-owui-clientSecret-hint').text()).toBe(L.signIn.secretStored)
    const test = vi.spyOn(api.setup, 'testOpenWebUi').mockResolvedValue(openWebUiTest())
    await check(wrapper)
    expect(test).toHaveBeenLastCalledWith({ target: 'open-webui', providerUrl: null, clientId: null, providerName: null, clientSecretExpiresOn: null, clientSecret: null })

    await wrapper.get('#setup-owui-providerUrl').setValue(` ${CHAT_IDP} `)
    await wrapper.get('#setup-owui-providerName').setValue('Chatten')
    await wrapper.get('#setup-owui-clientSecret').setValue('s3cret')
    await wrapper.get('#setup-owui-clientSecretExpiresOn').setValue('2027-06-30')
    await check(wrapper)
    expect(test).toHaveBeenLastCalledWith({
      target: 'open-webui', providerUrl: CHAT_IDP, clientId: null, providerName: 'Chatten', clientSecretExpiresOn: '2027-06-30', clientSecret: 's3cret',
    })

    await wrapper.get('[data-test=remove-secret] input').setValue(true)
    expect(wrapper.get('#setup-owui-clientSecret-hint').text()).toBe(W.secretRemoved)
    await check(wrapper)
    expect(test).toHaveBeenLastCalledWith(expect.objectContaining({ clientSecret: '', clientSecretExpiresOn: null }))
    wrapper.unmount()
  })

  it('locks a value the deployment sets and sends null for it', async () => {
    const signIn = openWebUiSignIn({
      providerUrl: { value: CHAT_IDP, source: 'deployment' },
      clientSecret: { set: true, source: 'deployment', setAt: null, expiresOn: null, unreadable: false },
    })
    const wrapper = await resume(withChat({ signIn }))
    await step(wrapper, 'openWebUi')
    expect(input(wrapper, 'setup-owui-providerUrl').disabled).toBe(true)
    expect(input(wrapper, 'setup-owui-providerUrl').value).toBe(CHAT_IDP)
    expect(wrapper.get('#setup-owui-providerUrl-hint').text()).toBe(L.signIn.locked)
    expect(input(wrapper, 'setup-owui-clientSecret').disabled).toBe(true)
    expect(wrapper.find('[data-test=remove-secret]').exists()).toBe(false)
    expect(wrapper.find('#setup-owui-clientSecretExpiresOn').exists()).toBe(false)
    const test = vi.spyOn(api.setup, 'testOpenWebUi').mockResolvedValue(openWebUiTest({ clientSecret: 'deployment' }))
    await check(wrapper)
    expect(test).toHaveBeenLastCalledWith(expect.objectContaining({ providerUrl: null, clientSecret: null }))
    // The client id and name are still the wizard's, so there is something to save.
    expect(wrapper.find('[data-test=openwebui-save]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('needs a provider address while the sign-in provider is not known', async () => {
    const signIn = openWebUiSignIn({ providerUrl: { value: null, source: 'default' } })
    const wrapper = await resume(withChat({ signIn }))
    await step(wrapper, 'openWebUi')
    expect(wrapper.get('#setup-owui-providerUrl-hint').text()).toBe(W.providerUrlHint)
    const test = vi.spyOn(api.setup, 'testOpenWebUi')
    await check(wrapper)
    expect(wrapper.text()).toContain(W.providerUrlRequired)
    expect(test).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('shows an accepted client, and Save sends the checked values with the check\'s id', async () => {
    const wrapper = await resume(withChat())
    await step(wrapper, 'openWebUi')
    expect(saveDisabled(wrapper)).toBe(true)
    vi.spyOn(api.setup, 'testOpenWebUi').mockResolvedValue(openWebUiTest({ values: openWebUiValues({ providerName: 'Chatten' }) }))
    await wrapper.get('#setup-owui-providerName').setValue('Chatten')
    await check(wrapper)
    expect(wrapper.find('[data-test=openwebui-client-accepted]').exists()).toBe(true)
    expect(wrapper.get('[data-test=openwebui-result]').text()).toContain(L.openWebUiResult.discoveryOk)
    expect(saveDisabled(wrapper)).toBe(false)

    const state = vi.spyOn(api.setup, 'state')
    const calls = state.mock.calls.length
    const complete = vi.spyOn(api.setup, 'completeOpenWebUi').mockResolvedValue(saved(true))
    await wrapper.get('[data-test=openwebui-save]').trigger('click')
    await flushPromises()
    expect(complete).toHaveBeenCalledWith({ target: 'open-webui', ...openWebUiValues({ providerName: 'Chatten' }), testId: 'w1' })
    expect(complete.mock.calls[0]![0]).not.toHaveProperty('clientSecret')
    expect(wrapper.get('[data-test=openwebui-saved]').text()).toContain(W.savedVerified)
    expect(state.mock.calls.length).toBe(calls + 1)
    expect(wrapper.get('[data-test=next]').text()).toContain(W.continue)
    wrapper.unmount()
  })

  it('says a refused client failed the check and offers no Save', async () => {
    const wrapper = await resume(withChat())
    await step(wrapper, 'openWebUi')
    vi.spyOn(api.setup, 'testOpenWebUi').mockResolvedValue(
      openWebUiTest({ status: 'failed', reason: 'client_refused', result: { discovery: 'ok', clientAccepted: false, clientReason: 'client_refused' } }),
    )
    await check(wrapper)
    expect(wrapper.get('[data-test=openwebui-failed]').text()).toContain(SETUP_TEST_MESSAGES.client_refused)
    expect(wrapper.find('[data-test=openwebui-client-refused]').exists()).toBe(true)
    expect(saveDisabled(wrapper)).toBe(true)
    wrapper.unmount()
  })

  it('explains a client that could not be verified, still lets the person save, and says so when saved', async () => {
    const wrapper = await resume(withChat())
    await step(wrapper, 'openWebUi')
    vi.spyOn(api.setup, 'testOpenWebUi').mockResolvedValue(openWebUiTest({ result: { discovery: 'ok', clientAccepted: null, clientReason: 'tls' } }))
    await check(wrapper)
    expect(wrapper.find('[data-test=openwebui-client-unverified]').exists()).toBe(true)
    const help = wrapper.get('[data-test=openwebui-unverified-help]').text()
    expect(help).toContain(SETUP_TEST_MESSAGES.tls)
    expect(help).toContain(L.openWebUiResult.unverifiedHelp)
    expect(saveDisabled(wrapper)).toBe(false)
    vi.spyOn(api.setup, 'completeOpenWebUi').mockResolvedValue(saved(false))
    await wrapper.get('[data-test=openwebui-save]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-test=openwebui-saved]').text()).toContain(W.savedUnverified)
    wrapper.unmount()
  })

  it('says why a check was refused', async () => {
    const wrapper = await resume(withChat())
    await step(wrapper, 'openWebUi')
    const test = vi.spyOn(api.setup, 'testOpenWebUi')
    for (const reason of ['provider_url_invalid', 'secret_required', 'not_discovery']) {
      test.mockRejectedValueOnce(new ApiError(400, 'POST', { code: 'setup_test_refused', params: { reason } }))
      await check(wrapper)
      expect(wrapper.get('[data-test=openwebui-check-error]').text()).toContain(OPENWEBUI_CHECK_MESSAGES[reason] ?? SETUP_TEST_MESSAGES[reason])
    }
    wrapper.unmount()
  })

  it('needs a new check after a value or the secret changes', async () => {
    const wrapper = await resume(withChat({ latestTest: openWebUiTest() }))
    expect(heading(wrapper)).toBe(L.steps.openWebUi)
    expect(saveDisabled(wrapper)).toBe(false)
    await wrapper.get('#setup-owui-clientId').setValue('another-client')
    expect(wrapper.find('[data-test=openwebui-stale]').exists()).toBe(true)
    expect(saveDisabled(wrapper)).toBe(true)
    await wrapper.get('#setup-owui-clientId').setValue('')
    expect(saveDisabled(wrapper)).toBe(false)
    await wrapper.get('#setup-owui-clientSecret').setValue('typed-after')
    expect(saveDisabled(wrapper)).toBe(true)
    wrapper.unmount()
  })

  it('resumes after a reload with the check\'s values and result', async () => {
    const latest = openWebUiTest({ values: openWebUiValues({ providerUrl: CHAT_IDP, clientId: 'chat' }), clientSecret: 'removed' })
    const wrapper = await resume(withChat({ latestTest: latest }))
    expect(heading(wrapper)).toBe(L.steps.openWebUi)
    expect(input(wrapper, 'setup-owui-providerUrl').value).toBe(CHAT_IDP)
    expect(input(wrapper, 'setup-owui-clientId').value).toBe('chat')
    expect((wrapper.get('[data-test=remove-secret] input').element as HTMLInputElement).checked).toBe(true)
    expect(wrapper.find('[data-test=openwebui-client-accepted]').exists()).toBe(true)
    expect(wrapper.find('[data-test=openwebui-stale]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('without a passed sign-in test, says it must pass first and leads back to it; the server\'s refusal says the same', async () => {
    const wrapper = await resume(withChat({ latestTest: openWebUiTest() }, false))
    expect(heading(wrapper)).toBe(L.steps.openWebUi)
    expect(wrapper.get('[data-test=admin-test-first]').text()).toContain(SETUP_COMPLETE_MESSAGES.admin_test_required)
    expect(saveDisabled(wrapper)).toBe(true)
    await wrapper.get('[data-test=to-sign-in]').trigger('click')
    await flushPromises()
    expect(heading(wrapper)).toBe(L.steps.signIn)
    wrapper.unmount()

    const passed = await resume(withChat({ latestTest: openWebUiTest() }))
    vi.spyOn(api.setup, 'completeOpenWebUi').mockRejectedValue(new ApiError(409, 'POST', { code: 'setup_test_required', params: { reason: 'admin_test_required' } }))
    await passed.get('[data-test=openwebui-save]').trigger('click')
    await flushPromises()
    expect(passed.get('[data-test=openwebui-save-error]').text()).toContain(SETUP_COMPLETE_MESSAGES.admin_test_required)
    passed.unmount()
  })

  it('skips for now, and the summary says so; Finish still depends only on the sign-in test', async () => {
    const wrapper = await resume(withChat())
    await step(wrapper, 'openWebUi')
    expect(wrapper.get('[data-test=openwebui-skip-hint]').text()).toBe(W.skipHint)
    const next = wrapper.get('[data-test=next]')
    expect(next.text()).toContain(W.skip)
    await next.trigger('click')
    await flushPromises()
    expect(heading(wrapper)).toBe(L.steps.summary)
    expect(wrapper.get('[data-test=summary-openwebui-outcome]').text()).toBe(L.summary.openWebUi.outcome.skipped)
    expect(wrapper.get('[data-test=summary-openwebui-providerUrl]').text()).toContain(DISCOVERY)
    expect(wrapper.get('[data-test=summary-openwebui-providerUrl]').text()).toContain(L.summary.sources.default)
    expect(wrapper.get('[data-test=summary-openwebui-secret]').text()).toBe(L.summary.openWebUi.secretNone)
    expect(wrapper.get('[data-test=finish]').attributes('aria-disabled')).toBeUndefined()
    wrapper.unmount()
  })

  it('shows what the deployment sets read-only, still checks it, and has nothing to save', async () => {
    const signIn = openWebUiSignIn({
      providerUrl: { value: CHAT_IDP, source: 'deployment' },
      clientId: { value: 'chat', source: 'deployment' },
      providerName: { value: 'Chatten', source: 'deployment' },
      clientSecret: { set: true, source: 'deployment', setAt: null, expiresOn: null, unreadable: false },
    })
    const wrapper = await resume(withChat({ signIn }))
    await step(wrapper, 'openWebUi')
    expect(wrapper.get('[data-test=openwebui-all-deployment]').text()).toBe(W.allDeployment)
    for (const id of ['setup-owui-providerUrl', 'setup-owui-clientId', 'setup-owui-providerName', 'setup-owui-clientSecret']) expect(input(wrapper, id).disabled).toBe(true)
    expect(wrapper.find('[data-test=openwebui-save]').exists()).toBe(false)
    const test = vi.spyOn(api.setup, 'testOpenWebUi').mockResolvedValue(openWebUiTest({ clientSecret: 'deployment' }))
    await check(wrapper)
    expect(test).toHaveBeenCalledWith({ target: 'open-webui', providerUrl: null, clientId: null, providerName: null, clientSecretExpiresOn: null, clientSecret: null })
    expect(wrapper.find('[data-test=openwebui-client-accepted]').exists()).toBe(true)
    expect(wrapper.get('[data-test=next]').text()).toContain(W.continue)
    await wrapper.get('[data-test=next]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-test=summary-openwebui-outcome]').text()).toBe(L.summary.openWebUi.outcome.deployment)
    expect(wrapper.get('[data-test=summary-openwebui-clientId]').text()).toContain(L.summary.sources.deployment)
    expect(wrapper.get('[data-test=summary-openwebui-secret]').text()).toBe(L.summary.secret.deployment)
    wrapper.unmount()
  })

  it('lists the saved values in the summary as saved in this session', async () => {
    const wrapper = await resume(withChat({ latestTest: openWebUiTest({ values: openWebUiValues({ clientId: 'chat' }) }) }))
    const savedState = withChat({
      latestTest: openWebUiTest({ values: openWebUiValues({ clientId: 'chat' }) }),
      signIn: openWebUiSignIn({
        clientId: { value: 'chat', source: 'wizard' },
        clientSecret: { set: true, source: 'wizard', setAt: '2026-11-15T10:06:00Z', expiresOn: null, unreadable: false },
      }),
    })
    vi.spyOn(api.setup, 'state').mockResolvedValue(savedState)
    vi.spyOn(api.setup, 'completeOpenWebUi').mockResolvedValue(saved(true))
    await wrapper.get('[data-test=openwebui-save]').trigger('click')
    await flushPromises()
    await step(wrapper, 'summary')
    expect(wrapper.get('[data-test=summary-openwebui-outcome]').text()).toBe(L.summary.openWebUi.outcome.saved)
    expect(wrapper.get('[data-test=summary-openwebui-clientId]').text()).toContain('chat')
    expect(wrapper.get('[data-test=summary-openwebui-clientId]').text()).toContain(L.summary.sources.wizard)
    expect(wrapper.get('[data-test=summary-openwebui-secret]').text()).toBe(L.summary.openWebUi.secretStored)
    wrapper.unmount()
  })

  it('leaves Open WebUI out of the summary when the deployment does not run it', async () => {
    const wrapper = await resume(setupState({ latestTest: setupTest() }))
    await step(wrapper, 'summary')
    expect(wrapper.find('[data-test=summary-openwebui]').exists()).toBe(false)
    wrapper.unmount()
  })
})

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { api } from '@/api'
import { isApiError } from '@/api/client'
import type { OpenWebUiTest, SetupState } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { useAuthStore } from '@/stores/auth'
import { formatTime } from '@/utils/format'
import { APP, SETUP_WIZARD } from '@/utils/labels'
import { setupCompleteMessage, setupTestMessage } from '@/utils/messages'
import { problemMessage } from '@/utils/problem'
import SetupAddressesStep from './SetupAddressesStep.vue'
import SetupCodeStep from './SetupCodeStep.vue'
import SetupOpenWebUiStep from './SetupOpenWebUiStep.vue'
import SetupSignInStep from './SetupSignInStep.vue'
import SetupSummaryStep from './SetupSummaryStep.vue'
import {
  canFinish, completeRequestOf, isLocked, isOpenWebUiStale, isStale, newForm, newOpenWebUiForm, openWebUiAllLocked, presetOf, suggestsAuthority, testRequestOf,
  valuesOf,
  type OpenWebUiForm, type OpenWebUiOutcome, type ProviderPreset, type SetupForm,
} from './setupValues'
import { listenForTestDone, openTestWindow } from './testWindow'

// The setup wizard (admin design §5.11, identity design §19). The setup session is a cookie; the code and the secret live in
// component state only, never in storage or an address. When the session or the setup window ends, or a call answers 401,
// the wizard goes back to the code with a message and keeps the typed values except the secret; a 404 means setup closed.
const L = SETUP_WIZARD
const auth = useAuthStore()

type StepKey = 'code' | 'addresses' | 'signIn' | 'openWebUi' | 'summary'
/** Whether the deployment runs Open WebUI, as the latest state said; kept while the wizard is back at the code. */
const withOpenWebUi = ref(false)
/** The steps in order; Open WebUI's (admin design §5.11 step 4) only when the deployment runs it. */
const steps = computed<{ key: StepKey; label: string }[]>(() => [
  { key: 'code', label: L.steps.code },
  { key: 'addresses', label: L.steps.addresses },
  { key: 'signIn', label: L.steps.signIn },
  ...(withOpenWebUi.value ? [{ key: 'openWebUi' as const, label: L.steps.openWebUi }] : []),
  { key: 'summary', label: L.steps.summary },
])
const indexOf = (key: StepKey) => steps.value.findIndex((s) => s.key === key)

type Phase = 'loading' | 'wizard' | 'closed' | 'closedMeanwhile' | 'done'
const phase = ref<Phase>('loading')
const stepIndex = ref(0)
const current = computed(() => steps.value[stepIndex.value]!)
const state = ref<SetupState | null>(null)
const form = ref<SetupForm | null>(null)
const preset = ref<ProviderPreset>('keycloak')
/** Why the wizard is back at the code, or a failed read. */
const notice = ref<string | null>(null)
/** The secret changed since the latest test started (it is not among the test's values). */
const secretEdited = ref(false)
const testing = ref(false)
const testError = ref<string | null>(null)
const blockedUrl = ref<string | null>(null)
const cancelledTestId = ref<string | null>(null)
const finishing = ref(false)
const finishError = ref<string | null>(null)
const signInUrl = ref<string | null>(null)
const now = ref(Date.now())
const errors = useFormErrors({ authority: 'setup-authority', tenantId: 'setup-tenantId' })
let popup: Window | null = null
/** Open WebUI's form, as kalla-admin's: the secret lives here only. */
const openWebUiForm = ref<OpenWebUiForm | null>(null)
const openWebUiSecretEdited = ref(false)
/** After a save in this session: whether the check confirmed the client; null while nothing was saved. */
const openWebUiVerified = ref<boolean | null>(null)
const openWebUiSkipped = ref(false)

const test = computed(() => {
  const latest = state.value?.latestTest ?? null
  return latest && latest.testId !== cancelledTestId.value ? latest : null
})
const stale = computed(() => !!form.value && !!state.value && isStale(test.value, form.value, state.value.signIn, secretEdited.value))
const finishable = computed(() => canFinish(test.value, stale.value))
const savedValues = computed(() => {
  const t = test.value
  if (canFinish(t, stale.value)) return t.values
  return form.value && state.value ? valuesOf(form.value, state.value.signIn) : null
})
/** The session's latest kalla-admin test passed as a platform admin: what saving Open WebUI's values needs (not that it is current). */
const adminTestPassed = computed(() => test.value?.status === 'passed' && test.value.result?.isPlatformAdmin === true)
const openWebUiTest = computed(() => state.value?.openWebUi.latestTest ?? null)
const openWebUiStale = computed(() => !!openWebUiForm.value && !!state.value
  && isOpenWebUiStale(openWebUiTest.value, openWebUiForm.value, state.value.openWebUi.signIn, openWebUiSecretEdited.value))
const openWebUiLocked = computed(() => !!state.value && openWebUiAllLocked(state.value.openWebUi.signIn))
const openWebUiOutcome = computed<OpenWebUiOutcome | null>(() => {
  if (!state.value?.openWebUi.enabled) return null
  if (openWebUiLocked.value) return 'deployment'
  if (openWebUiVerified.value !== null) return 'saved'
  return openWebUiSkipped.value ? 'skipped' : 'none'
})
/** On Open WebUI's step, Next skips it unless its values were saved or the deployment sets them all. */
const skipsOpenWebUi = computed(() => current.value.key === 'openWebUi' && openWebUiOutcome.value !== 'saved' && openWebUiOutcome.value !== 'deployment')
const minutesLeft = computed(() => (state.value ? Math.max(0, Math.ceil((Date.parse(state.value.endsAt) - now.value) / 60_000)) : 0))

function reachable(index: number): boolean {
  return index === 0 ? !state.value : !!state.value
}

function goTo(index: number): void {
  if (index < 0 || index >= steps.value.length || !reachable(index)) return
  stepIndex.value = index
}

function focus(id: string): void {
  void nextTick(() => document.getElementById(id)?.focus())
}

watch(stepIndex, () => focus('setup-step-heading'))

function closePopup(): void {
  if (popup && !popup.closed) popup.close()
  popup = null
}

/** Back to the code with `message`: the session is gone; the typed values stay, the secret does not. */
function backToCode(message: string): void {
  state.value = null
  notice.value = message
  testing.value = false
  blockedUrl.value = null
  cancelledTestId.value = null
  closePopup()
  if (form.value) form.value.clientSecret = ''
  if (openWebUiForm.value) openWebUiForm.value.clientSecret = ''
  if (stepIndex.value === 0) focus('setup-step-heading')
  stepIndex.value = 0
}

function close(next: 'closed' | 'closedMeanwhile'): void {
  phase.value = next
  state.value = null
  form.value = null
  openWebUiForm.value = null
  closePopup()
}

/** A 401 (no setup session) goes back to the code, a 404 (setup closed) ends the wizard. Whether `error` was one of them. */
function lost(error: unknown): boolean {
  if (!isApiError(error)) return false
  if (error.status === 404) {
    close(state.value ? 'closedMeanwhile' : 'closed')
    return true
  }
  if (error.status === 401) {
    backToCode(L.sessionEnded)
    return true
  }
  return false
}

function apply(next: SetupState): void {
  state.value = next
  withOpenWebUi.value = next.openWebUi.enabled
  if (!form.value) {
    form.value = newForm(next.signIn, next.latestTest, next.gateway?.suggestedAuthority ?? null)
    preset.value = presetOf(form.value)
  } else if (!form.value.authority.trim() && !next.latestTest && suggestsAuthority(next.signIn, next.gateway?.suggestedAuthority)) {
    // The gateway's wizard was finished while this one was open (the link on the sign-in step): its address, into a field still empty.
    form.value.authority = next.gateway.suggestedAuthority
  }
  openWebUiForm.value ??= newOpenWebUiForm(next.openWebUi.signIn, next.openWebUi.latestTest)
}

async function refresh(): Promise<void> {
  if (!state.value) return
  try {
    apply(await api.setup.state())
  } catch (e) {
    // A failed poll is tried again on the next one.
    lost(e)
  }
}

async function onStarted(): Promise<void> {
  notice.value = null
  try {
    apply(await api.setup.state())
    goTo(indexOf('addresses'))
  } catch (e) {
    if (!lost(e)) notice.value = problemMessage(e)
  }
}

function checkExpiry(): void {
  if (!state.value) return
  if (now.value >= Date.parse(state.value.endsAt)) backToCode(L.windowEnded)
  else if (now.value >= Date.parse(state.value.sessionExpiresAt)) backToCode(L.sessionEnded)
}

const clock = setInterval(() => {
  now.value = Date.now()
  checkExpiry()
}, 1000)

// While a test waits for its window: the done page tells this page, and every 2 s a read of the state catches what it can't.
let poll: ReturnType<typeof setInterval> | undefined
watch(() => phase.value === 'wizard' && test.value?.status === 'pending', (pending) => {
  clearInterval(poll)
  if (pending) poll = setInterval(() => void refresh(), 2000)
}, { immediate: true })
const stopListening = listenForTestDone(() => void refresh())
// Back on this tab, for example from the gateway's wizard in the tab the sign-in step's link opened.
function onVisible(): void {
  if (document.visibilityState === 'visible' && phase.value === 'wizard') void refresh()
}
document.addEventListener('visibilitychange', onVisible)

watch(() => [form.value?.clientSecret, form.value?.removeSecret] as const, (next, previous) => {
  if (next[0] !== undefined && previous[0] !== undefined) secretEdited.value = true
})
watch(() => [openWebUiForm.value?.clientSecret, openWebUiForm.value?.removeSecret] as const, (next, previous) => {
  if (next[0] !== undefined && previous[0] !== undefined) openWebUiSecretEdited.value = true
})

onMounted(async () => {
  if (auth.user?.setup.open === false) {
    phase.value = 'closed'
    return
  }
  try {
    apply(await api.setup.state())
    phase.value = 'wizard'
    // A reload with a live session: on at the addresses, at the sign-in when a test was made, at Open WebUI's step when it was checked.
    const s = state.value
    stepIndex.value = indexOf(s?.openWebUi.enabled && s.openWebUi.latestTest ? 'openWebUi' : s?.latestTest ? 'signIn' : 'addresses')
  } catch (e) {
    if (isApiError(e) && e.status === 404) {
      phase.value = 'closed'
      return
    }
    phase.value = 'wizard'
    if (!isApiError(e) || e.status !== 401) notice.value = problemMessage(e)
  }
})

onBeforeUnmount(() => {
  clearInterval(clock)
  clearInterval(poll)
  stopListening()
  document.removeEventListener('visibilitychange', onVisible)
  closePopup()
})

async function startTest(): Promise<void> {
  const s = state.value
  const f = form.value
  if (!s || !f || testing.value) return
  testError.value = null
  const ok = errors.validate({
    authority: !isLocked(s.signIn, 'authority') && !f.authority.trim() && L.signIn.authorityRequired,
    tenantId: preset.value === 'entra' && !isLocked(s.signIn, 'tenantId') && !f.tenantId.trim() && L.signIn.tenantRequired,
  })
  if (!ok) return
  // Opened by the click itself; the provider's address follows once the test is made.
  closePopup()
  popup = openTestWindow()
  testing.value = true
  blockedUrl.value = null
  try {
    const started = await api.setup.test(testRequestOf(f, s.signIn))
    secretEdited.value = false
    cancelledTestId.value = null
    if (popup && !popup.closed) popup.location.href = started.authorizeUrl
    else blockedUrl.value = started.authorizeUrl
    await refresh()
  } catch (e) {
    closePopup()
    if (lost(e)) return
    if (isApiError(e) && e.problem?.code === 'setup_test_refused') testError.value = setupTestMessage(e.problem.params?.reason ?? '')
    else {
      errors.applyServerError(e)
      testError.value = errors.formError.value
    }
  } finally {
    testing.value = false
  }
}

function cancelTest(): void {
  cancelledTestId.value = test.value?.testId ?? null
  blockedUrl.value = null
  closePopup()
}

async function finish(): Promise<void> {
  const t = test.value
  if (finishing.value || !canFinish(t, stale.value)) return
  finishing.value = true
  finishError.value = null
  try {
    signInUrl.value = (await api.setup.complete(completeRequestOf(t))).signInUrl
    phase.value = 'done'
    state.value = null
    form.value = null
    focus('setup-done-heading')
  } catch (e) {
    if (lost(e)) return
    if (isApiError(e) && e.problem?.code === 'setup_test_required') {
      finishError.value = setupCompleteMessage(e.problem.params?.reason ?? '')
      await refresh()
    } else finishError.value = problemMessage(e)
  } finally {
    finishing.value = false
  }
}

function onOpenWebUiChecked(checked: OpenWebUiTest): void {
  const s = state.value
  if (!s) return
  state.value = { ...s, openWebUi: { ...s.openWebUi, latestTest: checked } }
  openWebUiSecretEdited.value = false
}

async function onOpenWebUiSaved(verified: boolean): Promise<void> {
  openWebUiVerified.value = verified
  openWebUiSkipped.value = false
  await refresh()
}

function next(): void {
  if (skipsOpenWebUi.value) openWebUiSkipped.value = true
  goTo(stepIndex.value + 1)
}

function stepDone(index: number): boolean {
  const key = steps.value[index]!.key
  return (key === 'code' && !!state.value) || (key === 'signIn' && finishable.value) || (key === 'openWebUi' && openWebUiOutcome.value === 'saved')
}
</script>

<template>
  <div class="grid gap-6 pt-4" data-test="setup-wizard">
    <header>
      <h1 id="page-title" tabindex="-1" class="text-title-2 outline-none">{{ L.title }}</h1>
      <p class="mt-1 text-fg-2">{{ L.lead }}</p>
      <p v-if="state?.kind === 'reopen'" class="mt-2 text-small text-fg-2" data-test="reopened">{{ L.reopened }}</p>
      <p v-if="state" class="mt-2 text-small text-fg-3" data-test="window-ends">{{ L.windowEnds(formatTime(state.endsAt), minutesLeft) }}</p>
    </header>

    <p v-if="phase === 'loading'" role="status" class="text-small text-fg-3">{{ APP.loading }}</p>

    <section v-else-if="phase === 'closed' || phase === 'closedMeanwhile'" class="material grid gap-3 rounded-card p-6" aria-labelledby="setup-closed-heading" data-test="setup-closed">
      <h2 id="setup-closed-heading" class="text-heading">{{ phase === 'closed' ? L.closed.title : L.closed.closedMeanwhile }}</h2>
      <p class="text-fg-2">{{ phase === 'closed' ? L.closed.body : L.closed.closedMeanwhileBody }}</p>
      <div>
        <RouterLink v-slot="{ href, navigate }" to="/" custom>
          <UiButton variant="primary" icon="login" :href="href" @click="navigate">{{ L.closed.signIn }}</UiButton>
        </RouterLink>
      </div>
    </section>

    <section v-else-if="phase === 'done'" class="material grid gap-3 rounded-card p-6" aria-labelledby="setup-done-heading" data-test="setup-done">
      <h2 id="setup-done-heading" tabindex="-1" class="text-heading outline-none">{{ L.summary.done }}</h2>
      <p class="text-fg-2">{{ L.summary.doneBody }}</p>
      <div><UiButton v-if="signInUrl" variant="primary" size="lg" icon="login" :href="signInUrl" data-test="done-sign-in">{{ L.summary.signIn }}</UiButton></div>
    </section>

    <template v-else>
      <nav :aria-label="L.stepsLabel">
        <ol class="flex flex-wrap gap-2" data-test="stepper">
          <li v-for="(s, index) in steps" :key="s.key">
            <button
              type="button" class="inline-flex min-h-8 items-center gap-2 rounded-control px-2 text-small disabled:cursor-not-allowed"
              :class="index === stepIndex ? 'bg-surface font-medium text-fg shadow-1' : 'text-fg-2 hover:text-fg disabled:opacity-60'"
              :aria-current="index === stepIndex ? 'step' : undefined" :disabled="index !== stepIndex && !reachable(index)" :data-test="`stepper-${s.key}`"
              @click="goTo(index)"
            >
              <span class="grid size-6 place-items-center rounded-full bg-sunken text-caption" aria-hidden="true">{{ index + 1 }}</span>
              {{ s.label }}<span v-if="stepDone(index)" class="sr-only">{{ L.stepDone }}</span>
            </button>
          </li>
        </ol>
      </nav>

      <section class="material grid gap-5 rounded-card p-6" aria-labelledby="setup-step-heading">
        <div>
          <p class="text-caption text-fg-3" data-test="step-of">{{ L.stepOf(stepIndex + 1, steps.length) }}</p>
          <h2 id="setup-step-heading" tabindex="-1" class="text-title-3 outline-none" data-test="step-heading">{{ current.label }}</h2>
        </div>
        <InlineError v-if="notice" tone="warn" :message="notice" data-test="setup-notice" />
        <SetupCodeStep v-if="current.key === 'code'" @started="onStarted" @closed="close('closed')" />
        <template v-else-if="state && form">
          <SetupAddressesStep v-if="current.key === 'addresses'" :state="state" />
          <SetupSignInStep
            v-else-if="current.key === 'signIn'" v-model="form" v-model:preset="preset" :state="state" :test="test" :stale="stale" :testing="testing"
            :blocked-url="blockedUrl" :errors="errors.errors" :test-error="testError" @test="startTest" @cancel="cancelTest"
          />
          <SetupOpenWebUiStep
            v-else-if="current.key === 'openWebUi' && openWebUiForm" v-model="openWebUiForm" :state="state" :test="openWebUiTest" :stale="openWebUiStale"
            :admin-test-passed="adminTestPassed" :saved-verified="openWebUiVerified" :lost="lost" @checked="onOpenWebUiChecked" @saved="onOpenWebUiSaved"
            @to-sign-in="goTo(indexOf('signIn'))"
          />
          <SetupSummaryStep
            v-else-if="current.key === 'summary' && savedValues" :state="state" :form="form" :values="savedValues" :test="test" :finishable="finishable"
            :finishing="finishing" :error="finishError" :open-web-ui-outcome="openWebUiOutcome" @finish="finish"
          />
          <div class="flex flex-wrap gap-3 border-t border-border pt-4">
            <UiButton v-if="stepIndex > 1" icon="arrow_back" data-test="back" @click="goTo(stepIndex - 1)">{{ L.back }}</UiButton>
            <UiButton v-if="stepIndex < steps.length - 1" :variant="skipsOpenWebUi ? 'secondary' : 'primary'" icon="arrow_forward" data-test="next" @click="next">
              {{ skipsOpenWebUi ? L.openWebUi.skip : current.key === 'openWebUi' ? L.openWebUi.continue : L.next }}
            </UiButton>
          </div>
        </template>
      </section>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { OidcWizardValues, SetupState, SetupTest, SignInValueSource } from '@/api/types'
import DetailRow from '@/components/ui/DetailRow.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { COMMON, SETUP_WIZARD } from '@/utils/labels'
import { isLocked, REFUSE_GUESTS_DEFAULT, secretOf, type SetupForm, type TextFieldName } from './setupValues'

// Step "Summary and finish" (admin design §5.11): the values that are saved, each with its source, the tested person, and
// what the deployment set. Finish needs a passed test by a platform admin that still stands for the form (`canFinish`).
const props = defineProps<{
  state: SetupState
  form: SetupForm
  /** The values that are saved: the tested ones when Finish is possible, else the form's. */
  values: OidcWizardValues
  test: SetupTest | null
  finishable: boolean
  finishing: boolean
  error: string | null
}>()
defineEmits<{ (e: 'finish'): void }>()

const L = SETUP_WIZARD.summary
const S = SETUP_WIZARD.signIn
const A = SETUP_WIZARD.addresses
const signIn = computed(() => props.state.signIn)

interface Row { key: string; label: string; value: string; source: SignInValueSource }

const FIELDS: { field: TextFieldName; label: string }[] = [
  { field: 'authority', label: S.authority },
  { field: 'clientId', label: S.clientId },
  { field: 'tenantId', label: S.tenantId },
  { field: 'roleClaim', label: S.roleClaim },
  { field: 'groupClaim', label: S.groupClaim },
  { field: 'departmentClaim', label: S.departmentClaim },
  { field: 'teamClaim', label: S.teamClaim },
]

/** A value's row: the deployment's, the one set here, or the default (named when the API reports it). */
function row(key: string, label: string, own: string | null, live: { value: string | null; source: SignInValueSource }): Row {
  if (live.source === 'deployment') return { key, label, value: live.value ?? '', source: 'deployment' }
  if (own !== null) return { key, label, value: own, source: 'wizard' }
  return { key, label, value: live.source === 'default' ? (live.value ?? '') : '', source: 'default' }
}

const rows = computed<Row[]>(() => {
  const s = signIn.value
  const v = props.values
  const scopes = s.extraScopes
  return [
    ...FIELDS.map(({ field, label }) => row(field, label, v[field], s[field])),
    row('extraScopes', S.extraScopes, v.extraScopes?.join(' ') ?? null, { value: scopes.value.join(' '), source: scopes.source }),
    isLocked(s, 'refuseGuests')
      ? { key: 'refuseGuests', label: S.refuseGuests, value: s.refuseGuests.value ? COMMON.yes : COMMON.no, source: 'deployment' }
      : {
          key: 'refuseGuests', label: S.refuseGuests, value: (v.refuseGuests ?? REFUSE_GUESTS_DEFAULT) ? COMMON.yes : COMMON.no,
          source: v.refuseGuests === null ? 'default' : 'wizard',
        },
  ]
})

/** Where the saved secret comes from: the test's answer, else what the form would send. */
const secretText = computed(() => {
  if (isLocked(signIn.value, 'clientSecret')) return L.secret.deployment
  const source = props.finishable && props.test?.clientSecret ? props.test.clientSecret : null
  if (source) return L.secret[source]
  const typed = secretOf(props.form, signIn.value)
  if (typed === '') return L.secret.removed
  if (typed !== null) return L.secret.typed
  return signIn.value.clientSecret.set ? L.secret.stored : L.secret.none
})
const tone = (source: SignInValueSource) => (source === 'deployment' ? 'accent' : source === 'wizard' ? 'ok' : 'neutral')
const person = computed(() => props.test?.result ?? null)
</script>

<template>
  <div class="grid gap-6" data-test="summary-step">
    <section aria-labelledby="setup-values-title">
      <h3 id="setup-values-title" class="text-heading">{{ L.valuesTitle }}</h3>
      <p class="mt-1 text-small text-fg-3">{{ L.lead }}</p>
      <dl class="mt-2" data-test="summary-values">
        <DetailRow v-for="r in rows" :key="r.key" :label="r.label">
          <span class="flex flex-wrap items-center gap-2" :data-test="`summary-${r.key}`">
            <span v-if="r.value" class="break-all font-mono text-small">{{ r.value }}</span>
            <UiBadge :tone="tone(r.source)" :label="L.sources[r.source]" :dot="false" />
          </span>
        </DetailRow>
        <DetailRow :label="S.clientSecret"><span data-test="summary-secret">{{ secretText }}</span></DetailRow>
        <DetailRow v-if="values.clientSecretExpiresOn" :label="S.expiresOn">{{ values.clientSecretExpiresOn }}</DetailRow>
      </dl>
    </section>
    <section aria-labelledby="setup-deployment-summary">
      <h3 id="setup-deployment-summary" class="text-heading">{{ A.deploymentTitle }}</h3>
      <dl class="mt-2">
        <DetailRow :label="A.gateway">{{ state.deployment.gatewayBaseUrl ?? A.notSet }}</DetailRow>
        <DetailRow :label="A.apiPublicUrl">{{ state.deployment.apiPublicUrl ?? A.notSet }}</DetailRow>
        <DetailRow :label="A.platformAdminGroups">{{ state.deployment.platformAdminGroups.join(', ') || A.notSet }}</DetailRow>
      </dl>
    </section>
    <dl>
      <DetailRow :label="L.testedAs">
        <span data-test="summary-person">{{ person ? `${person.name ?? person.subject} (${person.subject})` : L.notTested }}</span>
      </DetailRow>
    </dl>
    <div class="grid gap-2">
      <div class="flex flex-wrap items-center gap-3">
        <UiButton variant="primary" size="lg" icon="check" :disabled="!finishable" :loading="finishing" :aria-describedby="finishable ? undefined : 'setup-finish-needs'" data-test="finish" @click="$emit('finish')">
          {{ L.finish }}
        </UiButton>
        <span v-if="!finishable" id="setup-finish-needs" class="text-small text-fg-3">{{ L.finishNeeds }}</span>
      </div>
      <InlineError v-if="error" :message="error" data-test="finish-error" />
    </div>
  </div>
</template>

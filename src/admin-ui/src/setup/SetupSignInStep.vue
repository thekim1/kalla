<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { SetupState, SetupTest } from '@/api/types'
import CheckField from '@/components/ui/CheckField.vue'
import InlineError from '@/components/ui/InlineError.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { gatewayHref } from '@/utils/gateway'
import { COMMON, SETUP_WIZARD } from '@/utils/labels'
import SetupSecretField from './SetupSecretField.vue'
import SetupTestPanel from './SetupTestPanel.vue'
import { applyPreset, isLocked, suggestsAuthority, type ProviderPreset, type SetupForm, type SignInField, type TextFieldName } from './setupValues'

// Step 3 (admin design §5.11): the provider, the client and its secret, the Advanced token shape with a preset, and the test
// sign-in. A value the deployment sets is shown locked. The secret field is never prefilled. The provider address may come from
// the gateway, which signs in at the same provider (identity design §19): the hint says so while the field holds it, and a gateway
// that has the feature but no address yet gets a note with a link to its own setup.
const props = defineProps<{
  state: SetupState
  test: SetupTest | null
  stale: boolean
  testing: boolean
  blockedUrl: string | null
  errors: Partial<Record<'authority' | 'tenantId', string>>
  /** Why the test could not start, or a refusal that names no field. */
  testError: string | null
}>()
const emit = defineEmits<{ (e: 'test'): void; (e: 'cancel'): void }>()
const form = defineModel<SetupForm>({ required: true })
const preset = defineModel<ProviderPreset>('preset', { required: true })

const L = SETUP_WIZARD.signIn
const signIn = computed(() => props.state.signIn)
const locked = (field: SignInField) => isLocked(signIn.value, field)
/** The hint of a field: locked, else the default it falls back to when left empty. */
function hintOf(field: TextFieldName, own?: string): string | undefined {
  if (locked(field)) return L.locked
  const value = signIn.value[field]
  return value.source === 'default' && value.value ? L.defaultHint(value.value) : own
}
const gateway = computed(() => props.state.gateway)
const fromGateway = computed(() => suggestsAuthority(signIn.value, gateway.value?.suggestedAuthority)
  && form.value.authority.trim() === gateway.value?.suggestedAuthority)
const gatewayWithoutAuthority = computed(() => !!gateway.value && !gateway.value.suggestedAuthority && !locked('authority'))
const gatewaySetupHref = computed(() => gatewayHref(gateway.value?.adminUrl, '/setup'))
const presetOptions = [
  { value: 'keycloak' as const, label: L.presets.keycloak },
  { value: 'entra' as const, label: L.presets.entra },
]

function choosePreset(value: ProviderPreset): void {
  preset.value = value
  applyPreset(form.value, signIn.value, value)
}

const pending = computed(() => props.test?.status === 'pending')
// Open when a value in it is set or refused, so the person sees it (and the refused field can take the focus).
const advancedOpen = ref(!!form.value.tenantId || !!form.value.roleClaim || !!form.value.groupClaim || !!form.value.departmentClaim || !!form.value.teamClaim
  || !!form.value.extraScopes || !form.value.refuseGuests)
watch(() => props.errors.tenantId, (error) => {
  if (error) advancedOpen.value = true
})
</script>

<template>
  <form class="grid gap-5" novalidate data-test="sign-in-step" @submit.prevent="emit('test')">
    <TextField
      id="setup-authority" v-model="form.authority" :label="L.authority" :hint="fromGateway ? L.authorityFromGateway : hintOf('authority', L.authorityHint)"
      :error="errors.authority" :disabled="locked('authority')" inputmode="url" mono
    />
    <p v-if="gatewayWithoutAuthority" class="text-small text-fg-3" data-test="gateway-sign-in-missing">
      {{ L.gatewayWithoutAuthority }}
      <a v-if="gatewaySetupHref" :href="gatewaySetupHref" target="_blank" rel="noopener noreferrer" class="underline" data-test="gateway-setup-link">
        {{ L.gatewaySetupLink }} <span class="sr-only">{{ COMMON.opensInNewTab }}</span>
      </a>
    </p>
    <TextField id="setup-clientId" v-model="form.clientId" :label="L.clientId" :hint="hintOf('clientId')" :disabled="locked('clientId')" mono />
    <SetupSecretField v-model="form" :secret="signIn.clientSecret" id-prefix="setup" :removed-hint="L.secretRemoved" />
    <details :open="advancedOpen" class="rounded-card border border-border p-3" data-test="advanced" @toggle="advancedOpen = ($event.target as HTMLDetailsElement).open">
      <summary class="min-h-6 cursor-pointer font-medium">{{ L.advanced }}</summary>
      <p class="mt-1 text-small text-fg-3">{{ L.advancedHint }}</p>
      <div class="mt-4 grid gap-5">
        <div class="grid gap-1.5">
          <span class="font-medium" aria-hidden="true">{{ L.preset }}</span>
          <SegmentedControl :model-value="preset" :options="presetOptions" :label="L.preset" mode="radio" data-test="preset" @update:model-value="choosePreset" />
          <span class="text-small text-fg-3">{{ preset === 'entra' ? L.entraHint : L.keycloakHint }}</span>
        </div>
        <TextField
          id="setup-tenantId" v-model="form.tenantId" :label="L.tenantId" :hint="hintOf('tenantId', L.tenantIdHint)" :error="errors.tenantId"
          :disabled="locked('tenantId')" mono
        />
        <TextField id="setup-roleClaim" v-model="form.roleClaim" :label="L.roleClaim" :hint="hintOf('roleClaim')" :disabled="locked('roleClaim')" mono />
        <TextField id="setup-groupClaim" v-model="form.groupClaim" :label="L.groupClaim" :hint="hintOf('groupClaim')" :disabled="locked('groupClaim')" mono />
        <TextField
          id="setup-departmentClaim" v-model="form.departmentClaim" :label="L.departmentClaim" :hint="hintOf('departmentClaim')"
          :disabled="locked('departmentClaim')" mono
        />
        <TextField id="setup-teamClaim" v-model="form.teamClaim" :label="L.teamClaim" :hint="hintOf('teamClaim')" :disabled="locked('teamClaim')" mono />
        <TextField
          id="setup-extraScopes" v-model="form.extraScopes" :label="L.extraScopes" :hint="locked('extraScopes') ? L.locked : L.extraScopesHint"
          :disabled="locked('extraScopes')" mono
        />
        <div class="grid gap-1">
          <CheckField v-model="form.refuseGuests" :label="L.refuseGuests" :description="L.refuseGuestsHint" :disabled="locked('refuseGuests')" data-test="refuse-guests" />
          <span v-if="locked('refuseGuests')" class="pl-1 text-small text-fg-3">{{ L.locked }}</span>
        </div>
      </div>
    </details>
    <div class="grid gap-2">
      <div class="flex flex-wrap items-center gap-3">
        <UiButton variant="primary" type="submit" icon="login" :loading="testing" :disabled="pending" data-test="test">{{ L.test }}</UiButton>
        <span class="text-small text-fg-3">{{ L.testHint }}</span>
      </div>
      <InlineError v-if="testError" :message="testError" data-test="test-error" />
    </div>
    <SetupTestPanel :test="test" :stale="stale" :platform-admin-groups="state.deployment.platformAdminGroups" :blocked-url="blockedUrl" @cancel="emit('cancel')" />
  </form>
</template>

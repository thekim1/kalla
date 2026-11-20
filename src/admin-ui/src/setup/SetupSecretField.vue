<script setup lang="ts" generic="T extends SecretFields">
import { computed } from 'vue'
import type { SignInSecretStatus } from '@/api/types'
import CheckField from '@/components/ui/CheckField.vue'
import TextField from '@/components/ui/TextField.vue'
import { SETUP_WIZARD } from '@/utils/labels'
import type { SecretFields } from './setupValues'

// A client secret in the setup wizard, kalla-admin's and Open WebUI's alike (identity design §19): never prefilled; left empty it
// keeps the stored one, "Remove the secret" tests without one (a public client), a typed one replaces it; a deployment's secret
// locks the field. The optional expiry date belongs to a secret kept by the wizard.
const props = defineProps<{
  secret: SignInSecretStatus
  /** The ids are `<idPrefix>-clientSecret` and `<idPrefix>-clientSecretExpiresOn`. */
  idPrefix: string
  /** The hint while "Remove the secret" is ticked: what the step's test and save then do. */
  removedHint: string
}>()
const form = defineModel<T>({ required: true })

const L = SETUP_WIZARD.signIn
const locked = computed(() => props.secret.source === 'deployment')
const hint = computed(() => {
  if (locked.value) return L.locked
  if (form.value.removeSecret) return props.removedHint
  if (props.secret.unreadable) return L.secretUnreadable
  return props.secret.set ? L.secretStored : L.secretNone
})
</script>

<template>
  <div class="grid gap-2">
    <TextField
      :id="`${idPrefix}-clientSecret`" v-model="form.clientSecret" type="password" :label="L.clientSecret" :hint="hint"
      :disabled="locked || form.removeSecret" data-test="client-secret"
    />
    <CheckField v-if="!locked" v-model="form.removeSecret" :label="L.removeSecret" data-test="remove-secret" />
  </div>
  <TextField
    v-if="!locked && !form.removeSecret" :id="`${idPrefix}-clientSecretExpiresOn`" v-model="form.clientSecretExpiresOn" type="date" :label="L.expiresOn"
    :hint="L.expiresOnHint" short
  />
</template>

<script setup lang="ts">
import { computed, watch } from 'vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiSwitch from '@/components/ui/UiSwitch.vue'
import { LEVEL_LABEL, PLATFORM_CLIENTS } from '@/utils/labels'
import { fieldsFor, historyAllowed, LEVELS, type ClientField, type ClientForm } from './clients'

// A client's fields for its kind (admin design §5.9 step 2), in the Add client drawer and in the client's drawer.
const props = defineProps<{ kind: string; idPrefix: string; errors: Partial<Record<ClientField, string>> }>()
const form = defineModel<ClientForm>({ required: true })

const L = PLATFORM_CLIENTS
const shown = computed(() => new Set(fieldsFor(props.kind)))
const id = (field: ClientField) => `${props.idPrefix}-${field}`
const levelOptions = LEVELS.map((l) => ({ value: l, label: LEVEL_LABEL[l] }))
const historyDisabled = computed(() => !historyAllowed(form.value.classificationCeiling))

// A sensitive client cannot keep history: the switch turns off with the reason under it.
watch(historyDisabled, (disabled) => {
  if (disabled) form.value.storesHistory = false
}, { immediate: true })
</script>

<template>
  <div class="grid gap-5">
    <TextField :id="id('name')" v-model="form.name" :label="L.fields.name" :error="errors.name" autocomplete="off" />
    <SelectField
      :id="id('classificationCeiling')" v-model="form.classificationCeiling" :label="L.fields.classificationCeiling" :options="levelOptions"
      :hint="L.hints.classificationCeiling" :error="errors.classificationCeiling"
    />
    <div v-if="shown.has('storesHistory')" class="grid gap-1" data-test="stores-history">
      <div class="flex items-center gap-3">
        <UiSwitch
          :id="id('storesHistory')" v-model="form.storesHistory" :labelledby="`${id('storesHistory')}-label`" :disabled="historyDisabled"
          :describedby="historyDisabled ? `${id('storesHistory')}-reason` : undefined"
        />
        <span :id="`${id('storesHistory')}-label`" class="font-medium">{{ L.fields.storesHistory }}</span>
      </div>
      <p v-if="historyDisabled" :id="`${id('storesHistory')}-reason`" class="text-small text-fg-3">{{ L.historySensitive }}</p>
      <p v-if="errors.storesHistory" class="text-small text-danger">{{ errors.storesHistory }}</p>
    </div>
    <TextField
      v-if="shown.has('clientRetentionDays')" :id="id('clientRetentionDays')" v-model="form.clientRetentionDays" :label="L.fields.clientRetentionDays"
      :hint="L.hints.clientRetentionDays" :error="errors.clientRetentionDays" inputmode="numeric"
    />
    <TextField
      v-if="shown.has('oAuthClientId')" :id="id('oAuthClientId')" v-model="form.oAuthClientId" :label="L.fields.oAuthClientId" :hint="L.hints.oAuthClientId"
      :error="errors.oAuthClientId" mono
    />
    <TextField
      v-if="shown.has('portalUrl')" :id="id('portalUrl')" v-model="form.portalUrl" :label="L.fields.portalUrl" :hint="L.hints.portalUrl" :error="errors.portalUrl"
      type="url" inputmode="url"
    />
    <div class="flex items-center gap-3">
      <UiSwitch :id="id('isEnabled')" v-model="form.isEnabled" :labelledby="`${id('isEnabled')}-label`" />
      <span :id="`${id('isEnabled')}-label`" class="font-medium">{{ L.fields.isEnabled }}</span>
    </div>
  </div>
</template>

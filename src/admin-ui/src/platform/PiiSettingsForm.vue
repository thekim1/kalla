<script setup lang="ts">
import { reactive, ref } from 'vue'
import { request } from '@/api/client'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiSwitch from '@/components/ui/UiSwitch.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { COMMON, PII_RULES as P } from '@/utils/labels'
import { PII_TYPES, piiRulesForm, piiRulesRequest, type PiiRulesForm } from './piiRules'

// The Personal data section of the Settings page (admin design §5.10; processing design §9.4): our own e-mail domains, the
// lowest name certainty, and the flag rules as a table, one row per type, with the list-of-people rule below. Every type
// is sent: a type the stored rules leave out never flags, so the table always says so explicitly. The server checks the
// rules as the worker reads them.
const props = defineProps<{ initial: Record<string, unknown>; url: string; submitLabel: string }>()
const emit = defineEmits<{ (e: 'saved', response: unknown): void; (e: 'cancel'): void }>()

// Mounted per load (the parent keys it), so the values are read from the props once.
const form = reactive<PiiRulesForm>(piiRulesForm(props.initial))
const saving = ref(false)
const ids: Record<string, string> = { ownDomains: 'pii-ownDomains', nerMinScore: 'pii-nerMinScore', flagRules: 'pii-rules', minNames: 'pii-minNames', minContacts: 'pii-minContacts' }
for (const t of PII_TYPES) ids[`min.${t}`] = `pii-min-${t}`
const errors = useFormErrors(ids)
const modeOptions = Object.entries(P.modes).map(([value, label]) => ({ value, label }))

async function save(): Promise<void> {
  const { body, problems } = piiRulesRequest(form)
  if (!errors.validate(problems)) return
  if (props.initial.version !== undefined && props.initial.version !== null) body.version = props.initial.version
  saving.value = true
  try {
    emit('saved', await request('PUT', props.url, { body }))
  } catch (e) {
    errors.applyServerError(e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <form id="settings-pii" class="grid gap-6" novalidate data-test="pii-settings" @submit.prevent="save">
    <TextAreaField id="pii-ownDomains" v-model="form.ownDomains" :label="P.ownDomains" :hint="P.ownDomainsHint" :error="errors.errors.ownDomains" :rows="3" mono />
    <TextField id="pii-nerMinScore" v-model="form.nerMinScore" :label="P.nerMinScore" :hint="P.nerMinScoreHint" :error="errors.errors.nerMinScore" inputmode="decimal" short />

    <fieldset id="pii-rules" class="grid gap-3" aria-describedby="pii-rules-hint" data-test="pii-rules">
      <legend class="mb-1 font-medium">{{ P.rules }}</legend>
      <p id="pii-rules-hint" class="text-small text-fg-3">{{ P.rulesHint }}</p>
      <table class="w-full text-left">
        <thead class="text-caption text-fg-3">
          <tr>
            <th scope="col" class="py-2 pr-4 font-medium">{{ P.type }}</th>
            <th scope="col" class="py-2 pr-4 font-medium">{{ P.flag }}</th>
            <th scope="col" class="py-2 font-medium">{{ P.min }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="t in PII_TYPES" :key="t" class="border-t border-border align-top" :data-test="`pii-rule-${t}`">
            <th :id="`pii-type-${t}`" scope="row" class="py-3 pr-3 font-normal max-sm:text-small">{{ P.types[t] }}</th>
            <td class="min-w-[9.5rem] py-2 pr-3">
              <!-- The row header names the select; the visible label would repeat the column header. -->
              <SelectField :id="`pii-flag-${t}`" v-model="form.types[t]!.flag" :label="`${P.flag}: ${P.types[t]}`" :options="modeOptions" class="[&_label]:sr-only" />
            </td>
            <td class="w-20 py-2 sm:w-36">
              <TextField
                v-if="form.types[t]!.flag === 'threshold'" :id="`pii-min-${t}`" v-model="form.types[t]!.min" :label="`${P.min}: ${P.types[t]}`"
                :error="errors.errors[`min.${t}`]" inputmode="numeric" short class="[&_label]:sr-only"
              />
            </td>
          </tr>
        </tbody>
      </table>
      <p v-if="errors.errors.flagRules" class="text-small text-danger">{{ errors.errors.flagRules }}</p>
    </fieldset>

    <fieldset class="grid gap-3" aria-describedby="pii-listlike-hint" data-test="pii-list-like">
      <legend class="mb-1 font-medium">{{ P.listLike }}</legend>
      <p id="pii-listlike-hint" class="text-small text-fg-3">{{ P.listLikeHint }}</p>
      <div class="flex items-center gap-3">
        <UiSwitch id="pii-listLike" v-model="form.listLike" labelledby="pii-listLike-label" />
        <span id="pii-listLike-label" class="font-medium">{{ P.listLikeOn }}</span>
      </div>
      <div v-if="form.listLike" class="grid gap-4 sm:grid-cols-2">
        <TextField id="pii-minNames" v-model="form.minNames" :label="P.minNames" :error="errors.errors.minNames" inputmode="numeric" short />
        <TextField id="pii-minContacts" v-model="form.minContacts" :label="P.minContacts" :error="errors.errors.minContacts" inputmode="numeric" short />
      </div>
    </fieldset>

    <InlineError v-if="errors.formError.value" :message="errors.formError.value" />
    <div class="flex gap-2">
      <UiButton variant="primary" type="submit" :loading="saving" data-test="save">{{ submitLabel }}</UiButton>
      <UiButton variant="quiet" @click="emit('cancel')">{{ COMMON.cancel }}</UiButton>
    </div>
  </form>
</template>

<script setup lang="ts">
import { nextTick, ref } from 'vue'
import { request } from '@/api/client'
import InlineError from '@/components/ui/InlineError.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { COMMON, PLATFORM } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import HostPolitenessTable from './HostPolitenessTable.vue'
import type { HostOverrides } from './politeness'

// The crawl section of the platform settings (admin design §5.8): its members as JSON, except the host politeness
// (`hostOverrides`, connectors design §6.6), which is the table below. Both go back in one save with the version read;
// the server checks the politeness (V37) and audits the change (`settings.update`).
const props = defineProps<{ initial: Record<string, unknown>; url: string; submitLabel: string }>()
const emit = defineEmits<{ (e: 'saved', response: unknown): void; (e: 'cancel'): void }>()

const value = (props.initial.value ?? {}) as Record<string, unknown>
const { hostOverrides: initialOverrides, ...rest } = value
const other = ref(JSON.stringify(rest, null, 2))
const overrides = ref<HostOverrides>({ ...((initialOverrides ?? {}) as HostOverrides) })
const jsonError = ref<string | null>(null)
const saveError = ref<string | null>(null)
const saving = ref(false)

async function save(): Promise<void> {
  jsonError.value = null
  saveError.value = null
  let parsed: unknown
  try {
    parsed = JSON.parse(other.value.trim() || '{}')
  } catch {
    parsed = null
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    jsonError.value = PLATFORM.form.invalidJson
    await nextTick()
    document.getElementById('platform-crawl-other')?.focus()
    return
  }
  const body: Record<string, unknown> = { value: { ...(parsed as Record<string, unknown>), hostOverrides: overrides.value } }
  if (props.initial.version !== undefined && props.initial.version !== null) body.version = props.initial.version
  saving.value = true
  try {
    emit('saved', await request('PUT', props.url, { body }))
  } catch (e) {
    saveError.value = problemMessage(e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="grid gap-6" data-test="crawl-settings">
    <!-- The table's drawer has a form of its own, so it stays outside this one. -->
    <form id="platform-crawl" novalidate @submit.prevent="save">
      <!-- The members without a field of their own, under Advanced as in every section (admin design §5.10). -->
      <details :open="!!jsonError" data-test="advanced-json">
        <summary class="cursor-pointer font-medium">{{ PLATFORM.form.advancedJson }}</summary>
        <TextAreaField
          id="platform-crawl-other" v-model="other" class="mt-3" :label="PLATFORM.politeness.otherSettings" :hint="PLATFORM.politeness.otherHint"
          :error="jsonError" :rows="8" mono
        />
      </details>
    </form>
    <HostPolitenessTable v-model="overrides" />
    <InlineError v-if="saveError" :message="saveError" />
    <div class="flex gap-2">
      <UiButton variant="primary" type="submit" form="platform-crawl" :loading="saving" data-test="save">{{ submitLabel }}</UiButton>
      <UiButton variant="quiet" @click="emit('cancel')">{{ COMMON.cancel }}</UiButton>
    </div>
  </div>
</template>

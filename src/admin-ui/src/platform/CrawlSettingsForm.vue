<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { request } from '@/api/client'
import InlineError from '@/components/ui/InlineError.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import SaveBar from '@/components/ui/SaveBar.vue'
import { useDirty } from '@/composables/useDirty'
import { PLATFORM } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import HostPolitenessTable from './HostPolitenessTable.vue'
import type { HostOverrides } from './politeness'

// The crawl section of the platform settings (admin design §5.8): its members as JSON, except the host politeness
// (`hostOverrides`, connectors design §6.6), which is the table below. Both go back in one save with the version read;
// the server checks the politeness (V37) and audits the change (`settings.update`).
// The save bar (admin §5.12 rule 7): Discard emits `cancel`, so the parent mounts the form again from the values loaded.
const props = defineProps<{ initial: Record<string, unknown>; url: string }>()
const emit = defineEmits<{ (e: 'saved', response: unknown): void; (e: 'cancel'): void; (e: 'dirty', dirty: boolean): void }>()

const value = (props.initial.value ?? {}) as Record<string, unknown>
const { hostOverrides: initialOverrides, ...rest } = value
const other = ref(JSON.stringify(rest, null, 2))
const overrides = ref<HostOverrides>({ ...((initialOverrides ?? {}) as HostOverrides) })
const jsonError = ref<string | null>(null)
const saveError = ref<string | null>(null)
const saving = ref(false)
// A changed host counts as one change, the other settings' JSON as one.
const dirty = useDirty(() => ({ other: other.value, hosts: { ...overrides.value } }))
dirty.reset()
watch(dirty.dirty, (d) => emit('dirty', d))
onBeforeUnmount(() => emit('dirty', false))

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
    <SaveBar :changes="dirty.changes.value" :saving="saving" @save="save" @discard="emit('cancel')" />
  </div>
</template>

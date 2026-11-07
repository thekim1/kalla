<script setup lang="ts">
import { computed, reactive } from 'vue'
import FieldGroup from '@/components/ui/FieldGroup.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { COMMON, PLATFORM } from '@/utils/labels'
import { PRESETS, fromForm, toForm, type FormProblems, type HostPoliteness, type PolitenessForm } from './politeness'

// One host's politeness profile (connectors design §6.6; admin design §5.8): a preset and the settings that override it.
// Apply puts the row in the table; the crawl section's save sends it, and the server checks it (V37).
const props = defineProps<{ open: boolean; host: string | null; profile: HostPoliteness; takenHosts: readonly string[] }>()
const emit = defineEmits<{
  (e: 'update:open', value: boolean): void
  (e: 'apply', value: { host: string; profile: HostPoliteness }): void
  (e: 'remove'): void
}>()

const L = PLATFORM.politeness
const F = L.fields
const O = L.options
// The parent mounts the drawer per host it opens (keyed), so the form is read from the props once.
const form = reactive<PolitenessForm>(toForm(props.host ?? '', props.profile))
const problems = reactive<FormProblems>({})
const id = (name: keyof PolitenessForm) => `politeness-${name}`

const fromPreset = { value: '', label: O.fromPreset }
const presetOptions = ['', ...PRESETS].map((p) => ({ value: p, label: L.presets[p] ?? p }))
const crawlDelayOptions = [fromPreset, { value: 'honour', label: O.honour }, { value: 'ignore', label: O.ignore }]
const latencyOptions = [fromPreset, { value: 'true', label: O.yes }, { value: 'false', label: O.no }]
const windowOptions = [fromPreset, { value: 'source', label: O.source }, { value: 'any', label: O.any }]
const breakerOptions = [{ value: '', label: O.breakerDefault }, { value: 'off', label: O.breakerOff }, { value: 'custom', label: O.breakerCustom }]
const robotsOptions = [{ value: 'respect', label: O.respect }, { value: 'ignore', label: O.ignoreRobots }]
const title = computed(() => (props.host ? L.editTitle(props.host) : L.newTitle))

function apply(): void {
  const { profile, problems: found } = fromForm(form)
  const host = form.host.trim()
  if (!found.host && host !== props.host && props.takenHosts.includes(host)) found.host = L.hostTaken
  for (const key of Object.keys(problems)) delete problems[key as keyof PolitenessForm]
  Object.assign(problems, found)
  const first = Object.keys(found)[0] as keyof PolitenessForm | undefined
  if (first) {
    document.getElementById(id(first))?.focus()
    return
  }
  emit('apply', { host, profile })
}
</script>

<template>
  <UiDrawer :open="open" :title="title" wide @update:open="emit('update:open', $event)">
    <form id="host-politeness" class="grid gap-6" novalidate data-test="politeness-form" @submit.prevent="apply">
      <div class="grid gap-5">
        <TextField
          :id="id('host')" v-model="form.host" :label="F.host" :hint="L.hints.host" :error="problems.host" mono autocomplete="off"
          :disabled="host !== null"
        />
        <SelectField :id="id('preset')" v-model="form.preset" :label="F.preset" :hint="L.hints.preset" :options="presetOptions" />
      </div>
      <FieldGroup :legend="L.title">
        <TextField
          :id="id('requestsPerSecond')" v-model="form.requestsPerSecond" :label="F.requestsPerSecond" :hint="L.hints.requestsPerSecond"
          :error="problems.requestsPerSecond" inputmode="decimal"
        />
        <TextField
          :id="id('maxConnections')" v-model="form.maxConnections" :label="F.maxConnections" :hint="L.hints.maxConnections"
          :error="problems.maxConnections" inputmode="numeric"
        />
        <SelectField :id="id('crawlDelay')" v-model="form.crawlDelay" :label="F.crawlDelay" :options="crawlDelayOptions" />
        <SelectField :id="id('adaptToLatency')" v-model="form.adaptToLatency" :label="F.adaptToLatency" :options="latencyOptions" />
        <SelectField :id="id('crawlWindow')" v-model="form.crawlWindow" :label="F.crawlWindow" :options="windowOptions" />
        <TextField
          :id="id('maxRetryAfterSeconds')" v-model="form.maxRetryAfterSeconds" :label="F.maxRetryAfterSeconds" :hint="L.hints.maxRetryAfterSeconds"
          :error="problems.maxRetryAfterSeconds" inputmode="numeric"
        />
        <TextField :id="id('maxFactor')" v-model="form.maxFactor" :label="F.maxFactor" :error="problems.maxFactor" inputmode="decimal" />
        <SelectField :id="id('breaker')" v-model="form.breaker" :label="F.breaker" :options="breakerOptions" />
        <template v-if="form.breaker === 'custom'">
          <TextField :id="id('threshold')" v-model="form.threshold" :label="F.threshold" :error="problems.threshold" inputmode="decimal" />
          <TextField :id="id('pauseMinutes')" v-model="form.pauseMinutes" :label="F.pauseMinutes" :error="problems.pauseMinutes" inputmode="numeric" />
        </template>
        <TextField
          :id="id('renderPerHost')" v-model="form.renderPerHost" :label="F.renderPerHost" :hint="L.hints.renderPerHost"
          :error="problems.renderPerHost" inputmode="numeric"
        />
      </FieldGroup>
      <div class="grid gap-5">
        <SelectField :id="id('robots')" v-model="form.robots" :label="F.robots" :options="robotsOptions" />
        <TextAreaField
          v-if="form.robots === 'ignore'" :id="id('reason')" v-model="form.reason" :label="F.reason" :hint="L.hints.reason" :error="problems.reason"
          :rows="2"
        />
        <TextField :id="id('userAgent')" v-model="form.userAgent" :label="F.userAgent" :hint="L.hints.userAgent" :error="problems.userAgent" mono />
      </div>
    </form>
    <template #footer>
      <UiButton variant="primary" type="submit" form="host-politeness" data-test="apply-host">{{ L.apply }}</UiButton>
      <UiButton v-if="host !== null" variant="danger" icon="delete" data-test="remove-host" @click="emit('remove')">{{ L.remove }}</UiButton>
      <UiButton variant="quiet" @click="emit('update:open', false)">{{ COMMON.cancel }}</UiButton>
    </template>
  </UiDrawer>
</template>

<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { api } from '@/api'
import type { Collection } from '@/api/types'
import CheckField from '@/components/ui/CheckField.vue'
import FieldGroup from '@/components/ui/FieldGroup.vue'
import InlineError from '@/components/ui/InlineError.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useUiStore } from '@/stores/ui'
import { formatDecimal } from '@/utils/format'
import { RETRIEVAL } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'

// Sökinställningar (admin design §7.1; V16 ranges, knowledge design §12.1): the collection's own values over the
// platform defaults. An empty field leaves the key out (the platform default applies); recency switched off is saved
// as an explicit null (an editor's choice that keeps the website template from adding it). Saved directly; the API
// starts the advisory evaluation run. **Prova utan att spara** runs the evaluation with the values as candidate settings.
const props = defineProps<{ collection: Collection; canEdit: boolean; proposal?: { coverageMaxDistance?: number; coverageMinKeywordScore?: number } | null }>()
const emit = defineEmits<{ (e: 'saved', collection: Collection): void; (e: 'tried'): void }>()
const ui = useUiStore()

type NumberKey = 'vectorWeight' | 'keywordWeight' | 'candidates' | 'maxChunksPerDocument' | 'coverageMaxDistance' | 'coverageMinKeywordScore' | 'expandMaxTokens'
interface Rule { min: number; max: number; integer?: boolean; positive?: boolean; fallback: string }
/** V16, and the code defaults shown as the platform's (`RetrievalSettings.Default`). */
const RULES: Record<NumberKey, Rule> = {
  vectorWeight: { min: 0, max: 5, fallback: '1,0' },
  keywordWeight: { min: 0, max: 5, fallback: '1,0' },
  candidates: { min: 20, max: 200, integer: true, fallback: '100' },
  maxChunksPerDocument: { min: 1, max: 10, integer: true, fallback: '3' },
  coverageMaxDistance: { min: 0.05, max: 1, fallback: '0,50' },
  coverageMinKeywordScore: { min: 0, max: Number.MAX_VALUE, positive: true, fallback: '' },
  expandMaxTokens: { min: 0, max: 4000, integer: true, fallback: '1 500' },
}
const KEYS = Object.keys(RULES) as NumberKey[]
const KIND = /^[a-z][a-z0-9-]{0,29}$/

const fields = reactive<Record<NumberKey, string>>(Object.fromEntries(KEYS.map((k) => [k, ''])) as Record<NumberKey, string>)
const recency = reactive({ on: false, halfLifeDays: '730', weight: '0,15', kinds: 'pdf' })
const errors = ref<Record<string, string>>({})
const error = ref<unknown>(null)
const saving = ref(false)
const trying = ref(false)
const proposalFilled = ref(false)

const num = (n: number) => formatDecimal(n, 4)
const parse = (text: string) => Number(text.replace(/\s/g, '').replace(',', '.'))

function reset(): void {
  const saved = props.collection.retrievalSettings ?? {}
  for (const k of KEYS) fields[k] = typeof saved[k] === 'number' ? num(saved[k] as number) : ''
  const r = saved.recency as { halfLifeDays?: number; weight?: number; kinds?: string[] } | null | undefined
  recency.on = !!r
  recency.halfLifeDays = r?.halfLifeDays != null ? num(r.halfLifeDays) : '730'
  recency.weight = r?.weight != null ? num(r.weight) : num(0.15)
  recency.kinds = r?.kinds?.join(', ') ?? 'pdf'
  errors.value = {}
  proposalFilled.value = false
}
watch(() => props.collection.version, reset, { immediate: true })
watch(
  () => props.proposal,
  (p) => {
    if (!p) return
    if (p.coverageMaxDistance != null) fields.coverageMaxDistance = num(p.coverageMaxDistance)
    if (p.coverageMinKeywordScore != null) fields.coverageMinKeywordScore = num(p.coverageMinKeywordScore)
    proposalFilled.value = true
  },
  { immediate: true },
)

function check(key: string, text: string, rule: { min: number; max: number; integer?: boolean; positive?: boolean }): number | undefined {
  const value = parse(text)
  const bad = !Number.isFinite(value) || (rule.positive ? value <= 0 : value < rule.min || value > rule.max) || (rule.integer && !Number.isInteger(value))
  if (bad) {
    errors.value[key] = rule.positive
      ? RETRIEVAL.invalidPositive
      : (rule.integer ? RETRIEVAL.invalidInteger : RETRIEVAL.invalidRange)(num(rule.min), num(rule.max))
    return undefined
  }
  return value
}

/** The settings object to send, or null when a field is invalid (errors shown per field). */
const build = (): Record<string, unknown> | null => {
  errors.value = {}
  const settings: Record<string, unknown> = {}
  for (const k of KEYS) {
    if (!fields[k].trim()) continue
    const value = check(k, fields[k], RULES[k])
    if (value !== undefined) settings[k] = value
  }
  if (recency.on) {
    const halfLifeDays = check('halfLifeDays', recency.halfLifeDays, { min: 30, max: 3650 })
    const weight = check('weight', recency.weight, { min: 0, max: 0.5 })
    const kinds = recency.kinds.split(',').map((k) => k.trim()).filter(Boolean)
    if (kinds.length === 0 || kinds.some((k) => !KIND.test(k))) errors.value.kinds = RETRIEVAL.invalidKinds
    settings.recency = { halfLifeDays, weight, kinds }
  } else {
    settings.recency = null
  }
  return Object.keys(errors.value).length ? null : settings
}
const hint = computed(() => (k: NumberKey) => (RULES[k].fallback ? RETRIEVAL.defaultHint(RULES[k].fallback) : RETRIEVAL.coverageMinKeywordScoreHint))

async function save(): Promise<void> {
  const settings = build()
  if (!settings) return
  saving.value = true
  error.value = null
  try {
    const saved = await api.collections.retrievalSettings(props.collection.id, settings, props.collection.version)
    ui.notify(RETRIEVAL.saved)
    emit('saved', saved)
  } catch (e) {
    error.value = e
  } finally {
    saving.value = false
  }
}

async function tryWithout(): Promise<void> {
  const settings = build()
  if (!settings) return
  trying.value = true
  error.value = null
  try {
    await api.evaluation.run(props.collection.id, { candidateSettings: settings })
    ui.notify(RETRIEVAL.tryStarted)
    emit('tried')
  } catch (e) {
    error.value = e
  } finally {
    trying.value = false
  }
}
</script>

<template>
  <UiCard :title="RETRIEVAL.title" data-test="retrieval">
    <p class="mb-4 text-fg-2">{{ RETRIEVAL.lead }}</p>
    <p v-if="proposalFilled" class="mb-4 font-medium" data-test="proposal-filled">{{ RETRIEVAL.proposalFilled }}</p>
    <form class="grid max-w-[680px] gap-8" novalidate @submit.prevent="save">
      <FieldGroup :legend="RETRIEVAL.weights">
        <TextField
          v-for="k in (['vectorWeight', 'keywordWeight', 'candidates', 'maxChunksPerDocument', 'expandMaxTokens'] as const)" :id="`rs-${k}`" :key="k"
          v-model="fields[k]" :label="RETRIEVAL[k]" :hint="hint(k)" :error="errors[k]" inputmode="decimal" short :disabled="!canEdit"
        />
      </FieldGroup>
      <FieldGroup :legend="RETRIEVAL.coverage" :hint="RETRIEVAL.coverageLead">
        <TextField
          v-for="k in (['coverageMaxDistance', 'coverageMinKeywordScore'] as const)" :id="`rs-${k}`" :key="k"
          v-model="fields[k]" :label="RETRIEVAL[k]" :hint="hint(k)" :error="errors[k]" inputmode="decimal" short :disabled="!canEdit"
        />
      </FieldGroup>
      <FieldGroup :legend="RETRIEVAL.recency" :hint="RETRIEVAL.recencyLead">
        <CheckField v-model="recency.on" :label="RETRIEVAL.recencyOn" :disabled="!canEdit" data-test="recency-on" />
        <template v-if="recency.on">
          <TextField id="rs-halfLifeDays" v-model="recency.halfLifeDays" :label="RETRIEVAL.halfLifeDays" :error="errors.halfLifeDays" inputmode="numeric" short :disabled="!canEdit" />
          <TextField id="rs-weight" v-model="recency.weight" :label="RETRIEVAL.weight" :error="errors.weight" inputmode="decimal" short :disabled="!canEdit" />
          <TextField id="rs-kinds" v-model="recency.kinds" :label="RETRIEVAL.kinds" :hint="RETRIEVAL.kindsHint" :error="errors.kinds" :disabled="!canEdit" />
        </template>
      </FieldGroup>
      <InlineError v-if="error" :message="problemMessage(error)" />
      <div v-if="canEdit" class="flex flex-wrap gap-2">
        <UiButton type="submit" variant="primary" :loading="saving" data-test="save-retrieval">{{ RETRIEVAL.save }}</UiButton>
        <UiButton :loading="trying" data-test="try-retrieval" @click="tryWithout">{{ RETRIEVAL.tryWithout }}</UiButton>
      </div>
    </form>
  </UiCard>
</template>

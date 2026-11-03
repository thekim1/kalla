<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { http } from '@/api/client'
import DataTable from '@/components/ui/DataTable.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useUiStore } from '@/stores/ui'
import { formatDateTime, formatDecimal } from '@/utils/format'
import { PLATFORM, type Tone } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'

interface StatusProfile {
  id: string
  name: string
  canaryRecorded: boolean
  canaryCheckedAt: string | null
  canarySimilarity: number | null
}

interface CheckResult {
  profileId: string
  profile: string
  similarity: number | null
  passed: boolean
  checkedAt: string | null
  error: string | null
  detail: string | null
}

interface CanaryRow {
  id: string
  name: string
  similarity: number | null
  checkedAt: string | null
  tone: Tone
  result: string
}

const C = PLATFORM.canary
const MIN_COSINE = 0.999
const ui = useUiStore()
const rows = ref<CanaryRow[]>([])
const loadError = ref<unknown>(null)
const checking = ref(false)
const checkError = ref<unknown>(null)

const columns = [
  { key: 'name', label: C.profile },
  { key: 'similarity', label: C.similarity, align: 'right' as const },
  { key: 'result', label: C.result },
  { key: 'checkedAt', label: C.checkedAt },
]
const empty = computed(() => rows.value.length === 0)

function fromStatus(p: StatusProfile): CanaryRow {
  const base = { id: p.id, name: p.name, similarity: p.canarySimilarity, checkedAt: p.canaryCheckedAt }
  if (!p.canaryRecorded) return { ...base, tone: 'neutral', result: C.notRecorded }
  if (p.canarySimilarity === null) return { ...base, tone: 'neutral', result: C.neverChecked }
  return p.canarySimilarity >= MIN_COSINE ? { ...base, tone: 'ok', result: C.passed } : { ...base, tone: 'danger', result: C.failed }
}

function fromCheck(r: CheckResult): CanaryRow {
  const base = { id: r.profileId, name: r.profile, similarity: r.similarity, checkedAt: r.checkedAt }
  if (r.error) {
    const text = C.errors[r.error] ?? C.errors.error!
    return { ...base, tone: 'warn', result: `${text}${r.detail ? ` (${r.detail})` : ''} ${C.notStored}` }
  }
  return r.passed ? { ...base, tone: 'ok', result: C.passed } : { ...base, tone: 'danger', result: C.failed }
}

async function load(): Promise<void> {
  loadError.value = null
  try {
    const status = await http.get<{ profiles: StatusProfile[] }>('/api/gateway/status')
    rows.value = status.profiles.map(fromStatus)
  } catch (e) {
    loadError.value = e
  }
}

async function check(): Promise<void> {
  checking.value = true
  checkError.value = null
  try {
    const results = await http.post<CheckResult[]>('/api/gateway/canary-check')
    const checked = new Map(results.map((r) => [r.profileId, fromCheck(r)]))
    const merged = rows.value.map((row) => checked.get(row.id) ?? row)
    rows.value = merged.length > 0 ? merged : [...checked.values()]
    ui.notify(C.done)
  } catch (e) {
    checkError.value = e
  } finally {
    checking.value = false
  }
}

onMounted(load)
</script>

<template>
  <UiCard :title="C.title" heading-id="embedding-canary-title" data-test="embedding-canary">
    <template #actions>
      <UiButton icon="fact_check" :disabled="checking" data-test="canary-check" @click="check">{{ checking ? C.checking : C.check }}</UiButton>
    </template>
    <p class="mb-4 text-fg-2">{{ C.lead }}</p>
    <InlineError v-if="loadError" :message="problemMessage(loadError)" class="mb-4" />
    <InlineError v-if="checkError" :message="problemMessage(checkError)" class="mb-4" />
    <p v-if="empty && !loadError" data-test="canary-empty">{{ C.none }}</p>
    <DataTable v-else-if="!empty" :columns="columns" :rows="rows" :row-key="(row) => row.id" :caption="C.title">
      <template #cell-name="{ row }">{{ row.name }}</template>
      <template #cell-similarity="{ row }">{{ row.similarity === null ? '–' : formatDecimal(row.similarity, 5, 5) }}</template>
      <template #cell-result="{ row }"><UiBadge :tone="row.tone" :label="row.result" /></template>
      <template #cell-checkedAt="{ row }">{{ row.checkedAt ? formatDateTime(row.checkedAt) : '–' }}</template>
    </DataTable>
  </UiCard>
</template>

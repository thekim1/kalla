<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { api } from '@/api'
import type { PreviewChunks, PreviewDocument, PreviewOutput, PreviewParsed, PreviewPii, StagePreviewResult } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiTabs from '@/components/ui/UiTabs.vue'
import { formatDateTime, formatMs, formatUsdCost } from '@/utils/format'
import { PREVIEW } from '@/utils/labels'
import { jobMessage } from '@/utils/messages'
import { isApiError } from '@/api/client'
import { problemMessage } from '@/utils/problem'

// The preview result (admin design §6.4, §5.4 step 5): the documents as tabs, each with its stage strip (state, duration,
// reuse, cost) and one output at a time: Före / Efter for a crawled page (the extraction without and with the site
// profile), or a stage's output (blocks, chunks with heading path and token count, personal-data counts). Outputs are
// fetched when shown, through GET /api/previews/{jobId}/documents/{index}/stages/{stage}/output.
const props = defineProps<{ jobId: string; result: StagePreviewResult }>()

type Output = PreviewOutput<PreviewParsed | PreviewChunks | PreviewPii>
const BEFORE_AFTER = 'before-after'

const tab = ref('d0')
const view = ref<string>(BEFORE_AFTER)
const outputs = reactive<Record<string, Output | undefined>>({})
const errors = reactive<Record<string, string | undefined>>({})

const index = computed(() => Number(tab.value.slice(1)))
const document = computed<PreviewDocument | undefined>(() => props.result.documents[index.value])
const tabs = computed(() => props.result.documents.map((d, i) => ({ value: `d${i}`, label: shortName(d, i) })))
const views = computed(() => {
  const d = document.value
  if (!d) return []
  const stages = d.stages.filter((s) => s.output).map((s) => ({ value: s.stage as string, label: PREVIEW.stageLabel[s.stage] ?? s.stage }))
  return d.siteProfile ? [{ value: BEFORE_AFTER, label: PREVIEW.beforeAfter }, ...stages] : stages
})
const profileLine = computed(() => {
  const p = document.value?.siteProfile
  if (!p) return null
  if (p.origin === 'manual') return PREVIEW.profileManual(p.host)
  return p.presetId ? PREVIEW.profilePreset(p.host, p.presetId) : PREVIEW.profileGeneric(p.host)
})

function shortName(d: PreviewDocument, i: number): string {
  if (!d.url) return PREVIEW.document(i + 1)
  try {
    const url = new URL(d.url)
    return `${url.hostname}${url.pathname === '/' ? '' : url.pathname}`
  } catch {
    return d.url
  }
}

const key = (stage: string) => `${index.value}/${stage}`

async function load(stage: string): Promise<void> {
  const k = key(stage)
  if (outputs[k] || errors[k]) return
  try {
    outputs[k] = await api.previews.output<PreviewParsed | PreviewChunks | PreviewPii>(props.jobId, index.value, stage)
  } catch (e) {
    errors[k] = isApiError(e) && e.status === 404 ? PREVIEW.expired : problemMessage(e)
  }
}

// The first view of a document: Före / Efter when it has a site profile, else the last stage with an output.
watch(
  [() => props.jobId, tab],
  () => {
    const available = views.value.map((v) => v.value)
    if (!available.includes(view.value)) view.value = available.includes(BEFORE_AFTER) ? BEFORE_AFTER : (available.at(-1) ?? '')
  },
  { immediate: true },
)
watch(
  [() => props.jobId, tab, view],
  () => {
    if (view.value === BEFORE_AFTER) {
      void load('before')
      void load('after')
    } else if (view.value) {
      void load(view.value)
    }
  },
  { immediate: true },
)

const shown = (stage: string) => outputs[key(stage)]
const parsed = (stage: string) => shown(stage)?.value as PreviewParsed | null | undefined
const chunks = computed(() => (view.value === 'chunk' ? (shown('chunk')?.value as PreviewChunks | null | undefined) : undefined))
const pii = computed(() => (view.value === 'personal-data' ? (shown('personal-data')?.value as PreviewPii | null | undefined) : undefined))
const tokens = computed(() => chunks.value?.chunks.reduce((sum, c) => sum + c.tokenCount, 0) ?? 0)
const piiCounts = computed(() =>
  Object.entries(pii.value?.counts ?? {}).filter((e): e is [string, number] => typeof e[1] === 'number' && e[1] > 0 && e[0] in PREVIEW.piiTypes),
)
const bodyBlocks = (p: PreviewParsed | null | undefined) => (p?.blocks ?? []).filter((b) => (b.layer ?? 'body') === 'body' && b.text.trim())
const stateTone = (state: string) => (state === 'succeeded' ? 'ok' : state === 'failed' ? 'danger' : 'neutral')
</script>

<template>
  <div class="grid gap-3" data-test="preview-result">
    <UiTabs v-if="tabs.length > 1" id="preview-docs" v-model="tab" :tabs="tabs" :label="PREVIEW.documents" />
    <div
      v-if="document" :id="`preview-docs-panel-${tab}`" :role="tabs.length > 1 ? 'tabpanel' : undefined"
      :aria-labelledby="tabs.length > 1 ? `preview-docs-tab-${tab}` : undefined" class="grid gap-3"
    >
      <p v-if="document.url" class="break-all font-mono text-small text-fg-2">{{ document.url }}</p>
      <ol :aria-label="PREVIEW.stages" class="flex flex-wrap gap-2" data-test="stage-strip">
        <li v-for="s in document.stages" :key="s.stage" class="rounded-card border border-border px-2 py-1 text-small">
          <span class="font-medium">{{ PREVIEW.stageLabel[s.stage] ?? s.stage }}</span>
          <UiBadge class="ml-1" :tone="stateTone(s.state)" :label="PREVIEW.stageState[s.state] ?? s.state" />
          <span class="ml-1 text-fg-3">{{ formatMs(s.durationMs ?? 0) }}</span>
          <span v-if="s.cacheHit" class="ml-1 text-fg-3">· {{ PREVIEW.reused }}</span>
          <span v-if="s.costUsd ?? s.estimatedCostUsd" class="ml-1 text-fg-3">· {{ formatUsdCost(s.costUsd ?? s.estimatedCostUsd) }}</span>
          <span v-if="s.error" class="block text-danger">{{ jobMessage(s.error) }}</span>
        </li>
      </ol>
      <p v-if="profileLine" class="text-small" data-test="site-profile">{{ profileLine }}</p>

      <SegmentedControl v-if="views.length > 1" v-model="view" :options="views" :label="PREVIEW.view" />

      <template v-if="view === 'before-after'">
        <InlineError v-if="errors[key('after')] || errors[key('before')]" :message="(errors[key('after')] ?? errors[key('before')])!" />
        <div v-else class="grid gap-3 md:grid-cols-2" data-test="before-after">
          <section v-for="side in (['before', 'after'] as const)" :key="side" class="min-w-0 rounded-card border border-border p-3">
            <h4 class="mb-2 font-medium">{{ side === 'before' ? PREVIEW.before : PREVIEW.after }}</h4>
            <dl v-if="side === 'after' && parsed('after')" class="mb-2 grid gap-1 text-small">
              <div v-if="parsed('after')!.breadcrumb?.length"><dt class="inline text-fg-2">{{ PREVIEW.breadcrumb }}: </dt><dd class="inline">{{ parsed('after')!.breadcrumb!.join(' › ') }}</dd></div>
              <div v-if="parsed('after')!.visibleDate"><dt class="inline text-fg-2">{{ PREVIEW.visibleDate }}: </dt><dd class="inline">{{ parsed('after')!.visibleDate }}</dd></div>
            </dl>
            <div class="max-h-96 overflow-y-auto text-small" :data-test="`text-${side}`">
              <template v-if="shown(side)">
                <p v-for="(b, i) in bodyBlocks(parsed(side))" :key="i" class="mb-2 whitespace-pre-wrap" :class="b.kind === 'heading' ? 'font-semibold' : ''">{{ b.text }}</p>
                <p v-if="!bodyBlocks(parsed(side)).length" class="text-fg-2">{{ PREVIEW.noText }}</p>
                <p v-if="shown(side)!.truncated" class="text-fg-2">{{ PREVIEW.truncated }}</p>
              </template>
            </div>
          </section>
        </div>
      </template>

      <template v-else-if="view">
        <InlineError v-if="errors[key(view)]" :message="errors[key(view)]!" />
        <div v-else-if="shown(view)" class="max-h-[32rem] overflow-y-auto rounded-card border border-border p-3 text-small" :data-test="`output-${view}`">
          <p v-if="shown(view)!.truncated" class="mb-2 text-fg-2">{{ PREVIEW.truncated }}</p>
          <template v-if="chunks">
            <p class="mb-2 font-medium">{{ PREVIEW.chunks(chunks.chunks.length, tokens) }}</p>
            <article v-for="c in chunks.chunks" :key="c.ordinal" class="mb-3 border-t border-border pt-2">
              <p class="text-fg-2">
                {{ c.headingPath || c.prefix }} · {{ PREVIEW.tokens(c.tokenCount) }}<span v-if="c.page != null"> · {{ PREVIEW.page(c.page) }}</span>
              </p>
              <p class="whitespace-pre-wrap">{{ c.text }}</p>
            </article>
          </template>
          <template v-else-if="pii">
            <p>{{ PREVIEW.piiState }}: {{ PREVIEW.piiStates[pii.state] ?? pii.state }}</p>
            <p v-if="!piiCounts.length" class="mt-2">{{ PREVIEW.piiNone }}</p>
            <dl v-else class="mt-2 grid gap-1" :aria-label="PREVIEW.piiCounts">
              <div v-for="[type, count] in piiCounts" :key="type" class="flex gap-2">
                <dt>{{ PREVIEW.piiTypes[type] }}</dt>
                <dd class="font-medium">{{ count }}</dd>
                <dd v-if="pii.typesFired.includes(type)" class="text-warn">{{ PREVIEW.piiFired }}</dd>
              </div>
            </dl>
          </template>
          <template v-else-if="parsed(view)">
            <p v-if="parsed(view)!.title" class="mb-2"><span class="text-fg-2">{{ PREVIEW.title }}: </span>{{ parsed(view)!.title }}</p>
            <p v-for="(b, i) in bodyBlocks(parsed(view))" :key="i" class="mb-2 whitespace-pre-wrap" :class="b.kind === 'heading' ? 'font-semibold' : ''">{{ b.text }}</p>
            <p v-if="!bodyBlocks(parsed(view)).length" class="text-fg-2">{{ PREVIEW.noText }}</p>
          </template>
          <p v-else class="text-fg-2">{{ PREVIEW.noOutput }}</p>
        </div>
      </template>
    </div>
    <p class="text-small text-fg-3">{{ PREVIEW.expires(formatDateTime(result.expiresAt)) }}</p>
  </div>
</template>

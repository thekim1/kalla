<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { CheckState, Collection, Level, PreflightResult, Source, SourceType, StagePreviewResult } from '@/api/types'
import FetchModeFields from '@/components/sources/FetchModeFields.vue'
import PreviewResult from '@/components/sources/PreviewResult.vue'
import { type FetchMode, READY_SELECTOR_MAX, withFetchSettings } from '@/components/sources/fetchMode'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { useJobPoll } from '@/composables/useJobPoll'
import { formatNumber } from '@/utils/format'
import { COMMON, LEVEL_LABEL, SOURCE, SOURCE_TYPE_LABEL } from '@/utils/labels'
import { jobMessage } from '@/utils/messages'
import { problemMessage } from '@/utils/problem'

// Lägg till källa (admin design §5.4 steps 3 to 6): type cards, the template parameters of the type
// (contracts/sources/v1/{type}.schema.json, with the schema's defaults), Kontrollera (preflight job, polled as §4.7; a
// failed check blocks saving; per rendered page the raw and rendered word counts and, when the raw page holds under half
// the text, "This site seems to need JavaScript" with a button that sets Fetch pages with to a browser, §5.5.3),
// Förhandsgranska (stage-preview of up to 5 of the addresses on the pipeline the source would get, §6.4: Före / Efter and
// each stage's output), then Spara och hämta: the source is created and its first sync enqueued.
const props = defineProps<{ open: boolean; collection: Collection }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'saved', source: Source, syncing: boolean): void }>()

const TYPES: SourceType[] = ['crawl', 'url-list', 'upload']
const LEVELS: Level[] = ['public', 'internal', 'sensitive']
/** contracts/sources/v1/crawl.schema.json defaults. */
const DEFAULT_EXCLUDE = ['**/sok?*', '**/search?*', '**?*sort=*', '**?*print=*', '**/login*', '**/logout*']

const type = ref<SourceType>('crawl')
const name = ref('')
const level = ref<Level>('public')
const urls = ref('')
const include = ref('')
const exclude = ref(DEFAULT_EXCLUDE.join('\n'))
// A modest first crawl (#285 review): the form starts at 500 pages; the schema's default (10,000) stays for the API.
const FIRST_CRAWL_PAGES = '500'
const maxPages = ref(FIRST_CRAWL_PAGES)
const maxDepth = ref('10')
const piiMode = ref<'hold' | 'report'>('hold')
const fetchMode = ref<FetchMode>('http')
const readySelector = ref('')
const scrollToLoad = ref(false)
const saving = ref(false)
const checkError = ref<unknown>(null)
const preflight = useJobPoll<PreflightResult>()
const preview = useJobPoll<StagePreviewResult>()
const previewError = ref<string | null>(null)
const previewStarting = ref(false)
/** The worker previews at most 5 documents (preview.limit_documents). */
const PREVIEW_URLS = 5
const form = useFormErrors({
  name: 'source-name', urls: 'source-urls', maxPages: 'source-max-pages', maxDepth: 'source-max-depth', readySelector: 'source-ready-selector',
})

const levelOptions = computed(() =>
  LEVELS.slice(0, LEVELS.indexOf(props.collection.classification) + 1).map((l) => ({ value: l, label: LEVEL_LABEL[l] })),
)
const piiOptions = (['hold', 'report'] as const).map((m) => ({ value: m, label: SOURCE.piiModes[m] }))
const lines = (text: string) => text.split('\n').map((l) => l.trim()).filter(Boolean)
const result = computed(() => (preflight.job.value?.state === 'succeeded' ? preflight.job.value.result : null))
const blocked = computed(() => result.value?.state === 'fail')
// The comparison is a hint (connectors design §8.1): shown while the form fetches with HTTP.
const needsJavaScript = computed(() => fetchMode.value === 'http' && !!result.value?.checks.some((c) => c.code === 'render.suggested'))
const previewResult = computed(() => (preview.job.value?.state === 'succeeded' ? preview.job.value.result : null))
const previewProblem = computed(() => previewError.value ?? (preview.error.value ? problemMessage(preview.error.value) : null))
const previewCapped = computed(() => lines(urls.value).length > PREVIEW_URLS)
const checkProblem = computed(() => {
  const e = preflight.error.value ?? checkError.value
  return e ? problemMessage(e) : null
})

watch(
  () => props.open,
  (open) => {
    if (!open) return
    type.value = 'crawl'
    name.value = ''
    level.value = props.collection.classification
    urls.value = ''
    include.value = ''
    exclude.value = DEFAULT_EXCLUDE.join('\n')
    maxPages.value = FIRST_CRAWL_PAGES
    maxDepth.value = '10'
    piiMode.value = 'hold'
    fetchMode.value = 'http'
    readySelector.value = ''
    scrollToLoad.value = false
    preflight.stop()
    preflight.job.value = null
    resetPreview()
    form.clear()
  },
)
// A changed address or scope makes the last check stale; a changed address or type the last preview.
watch([type, urls, include, exclude, maxPages, maxDepth], () => {
  preflight.stop()
  preflight.job.value = null
})
watch([type, urls], resetPreview)

function resetPreview(): void {
  preview.stop()
  preview.job.value = null
  previewError.value = null
}

function settings(): Record<string, unknown> {
  const piiPart = props.collection.classification === 'public' ? { piiMode: piiMode.value } : {}
  if (type.value === 'upload') return { ...piiPart }
  const fetch = { fetchMode: fetchMode.value, readySelector: readySelector.value, scrollToLoad: scrollToLoad.value }
  if (type.value === 'url-list') return withFetchSettings({ urls: lines(urls.value), ...piiPart }, fetch)
  return withFetchSettings({
    startUrls: lines(urls.value), include: lines(include.value), exclude: lines(exclude.value),
    maxPages: Number(maxPages.value), maxDepth: Number(maxDepth.value), ...piiPart,
  }, fetch)
}

/** A `render` entry is information (the comparison never blocks); the other checks show their state. */
function checkTone(check: string, state: CheckState): 'ok' | 'warn' | 'danger' | 'neutral' {
  if (state === 'warn') return 'warn'
  if (state === 'fail') return 'danger'
  return check === 'render' ? 'neutral' : 'ok'
}

function valid(forSave: boolean): boolean {
  if (type.value === 'upload') return form.validate({ name: !name.value.trim() && SOURCE.nameRequired })
  const addresses = lines(urls.value)
  const integer = (v: string) => /^\d+$/.test(v.trim())
  return form.validate({
    name: forSave && !name.value.trim() && SOURCE.nameRequired,
    urls: (addresses.length === 0 || addresses.some((u) => !/^https?:\/\//.test(u))) && SOURCE.urlsRequired,
    maxPages: type.value === 'crawl' && !integer(maxPages.value) && SOURCE.numberInvalid,
    maxDepth: type.value === 'crawl' && !integer(maxDepth.value) && SOURCE.numberInvalid,
    readySelector: fetchMode.value === 'browser' && readySelector.value.trim().length > READY_SELECTOR_MAX && SOURCE.readySelectorInvalid,
  })
}

async function check(): Promise<void> {
  if (!valid(false)) return
  checkError.value = null
  try {
    const started = await api.sources.preflight({ collectionId: props.collection.id, type: type.value, settings: settings() })
    preflight.start(started.jobId)
  } catch (e) {
    form.applyServerError(e)
  }
}

async function runPreview(): Promise<void> {
  if (!valid(false)) return
  resetPreview()
  previewStarting.value = true
  try {
    // The pipeline the source would get (the default for its type); the draft's version when it has one (§6.4).
    const pipelines = await api.pipelines.list({ collectionId: props.collection.id, sourceType: type.value })
    const pipeline = pipelines.find((p) => p.isDefault) ?? pipelines[0]
    if (!pipeline) {
      previewError.value = SOURCE.noPipeline
      return
    }
    const started = await api.pipelines.preview(pipeline.id, { collectionId: props.collection.id, urls: lines(urls.value).slice(0, PREVIEW_URLS) })
    preview.start(started.jobId)
  } catch (e) {
    previewError.value = problemMessage(e)
  } finally {
    previewStarting.value = false
  }
}

async function save(): Promise<void> {
  if (blocked.value || !valid(true)) return
  saving.value = true
  try {
    const source = await api.collections.addSource(props.collection.id, {
      type: type.value, name: name.value.trim(), classification: level.value, settings: settings(),
    })
    // Step 6: the first sync starts at once; if enqueueing fails the source still exists and Hämta nu is on its page. An upload
    // source has nothing to fetch yet: its files are uploaded on its page, and each upload starts a sync.
    const syncing = type.value === 'upload' ? false : await api.sources.sync(source.id).then(() => true, () => false)
    emit('saved', source, syncing)
    emit('update:open', false)
  } catch (e) {
    form.applyServerError(e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UiDrawer :open="open" :title="SOURCE.add" :subtitle="collection.name" wide @update:open="emit('update:open', $event)">
    <form id="source-create" class="grid gap-5" novalidate @submit.prevent="save">
      <fieldset class="grid gap-2">
        <legend class="mb-1 font-medium">{{ SOURCE.chooseType }}</legend>
        <label v-for="t in TYPES" :key="t" class="flex cursor-pointer gap-3 rounded-card border border-border p-3" :data-test="`type-${t}`">
          <input v-model="type" type="radio" name="source-type" :value="t" class="mt-1" />
          <span>
            <span class="block font-medium">{{ SOURCE_TYPE_LABEL[t] }}</span>
            <span class="block text-small text-fg-2">{{ SOURCE.typeHelp[t] }}</span>
          </span>
        </label>
      </fieldset>

      <TextField id="source-name" v-model="name" :label="SOURCE.name" :error="form.errors.name" autocomplete="off" />
      <SelectField id="source-level" v-model="level" :label="SOURCE.level" :hint="SOURCE.levelHint" :options="levelOptions" />
      <template v-if="type === 'upload'">
        <p class="rounded-card bg-sunken p-4" data-test="upload-later">{{ SOURCE.uploadLater }}</p>
        <SelectField
          v-if="collection.classification === 'public'" id="source-pii-upload" v-model="piiMode" :label="SOURCE.piiMode" :options="piiOptions"
        />
      </template>
      <template v-else>
        <TextAreaField
          id="source-urls" v-model="urls" :label="type === 'crawl' ? SOURCE.startUrls : SOURCE.urls"
          :hint="type === 'crawl' ? SOURCE.startUrlsHint : SOURCE.urlsHint" :error="form.errors.urls" mono
        />
        <FetchModeFields
          v-model:fetch-mode="fetchMode" v-model:ready-selector="readySelector" v-model:scroll-to-load="scrollToLoad" id-prefix="source"
          :ready-selector-error="form.errors.readySelector"
        />
        <details v-if="type === 'crawl'" class="rounded-card border border-border p-4">
          <summary class="cursor-pointer font-medium">{{ SOURCE.advanced }}</summary>
          <div class="mt-4 grid gap-5">
            <TextAreaField id="source-include" v-model="include" :label="SOURCE.include" :hint="SOURCE.includeHint" mono />
            <TextAreaField id="source-exclude" v-model="exclude" :label="SOURCE.exclude" :hint="SOURCE.excludeHint" mono />
            <TextField id="source-max-pages" v-model="maxPages" :label="SOURCE.maxPages" inputmode="numeric" :error="form.errors.maxPages" />
            <TextField id="source-max-depth" v-model="maxDepth" :label="SOURCE.maxDepth" inputmode="numeric" :error="form.errors.maxDepth" />
          </div>
        </details>
        <SelectField
          v-if="collection.classification === 'public'" id="source-pii" v-model="piiMode" :label="SOURCE.piiMode"
          :options="piiOptions"
        />

        <section :aria-label="SOURCE.checkResult" aria-live="polite">
          <UiButton icon="fact_check" :loading="preflight.polling.value" data-test="check" @click="check">{{ SOURCE.check }}</UiButton>
          <p v-if="preflight.polling.value" class="mt-2 text-small text-fg-2">{{ SOURCE.checking }}</p>
          <InlineError v-if="checkProblem" :message="checkProblem" class="mt-2" />
          <InlineError
            v-if="preflight.job.value && preflight.job.value.state !== 'succeeded' && preflight.job.value.lastError"
            :message="jobMessage(preflight.job.value.lastError)" class="mt-2"
          />
          <div v-if="result" class="mt-3 grid gap-2" data-test="checks">
            <p v-if="result.estimatedPages != null" class="text-small">{{ SOURCE.estimatedPages(formatNumber(result.estimatedPages)) }}</p>
            <div v-for="(c, i) in result.checks" :key="i" class="flex items-start gap-2">
              <UiBadge
                :tone="checkTone(c.check, c.state)" :label="c.check === 'render' && c.state === 'pass' ? SOURCE.checkInfo : SOURCE.checkStates[c.state]"
              />
              <span class="text-small">
                {{ jobMessage(c.code) }}
                <span v-if="c.facts?.rawWords != null && c.facts?.renderedWords != null" class="block" data-test="render-words">
                  {{ SOURCE.renderWords(formatNumber(c.facts.rawWords), formatNumber(c.facts.renderedWords)) }}
                </span>
                <span v-if="c.target" class="block text-fg-3">{{ c.target }}</span>
              </span>
            </div>
            <div v-if="needsJavaScript" class="flex flex-wrap items-center gap-3 rounded-card border border-warn p-3" data-test="needs-javascript">
              <p class="font-medium">{{ SOURCE.needsJavaScript }}</p>
              <UiButton data-test="use-browser" @click="fetchMode = 'browser'">{{ SOURCE.useBrowser }}</UiButton>
            </div>
            <p v-if="blocked" role="alert" class="text-small text-danger">{{ SOURCE.checkFailed }}</p>
          </div>
        </section>

        <section :aria-label="SOURCE.previewResult" aria-live="polite">
          <UiButton icon="preview" :loading="previewStarting || preview.polling.value" data-test="preview" @click="runPreview">{{ SOURCE.preview }}</UiButton>
          <p class="mt-2 text-small text-fg-2">{{ SOURCE.previewHint }}<template v-if="previewCapped"> {{ SOURCE.previewFirst }}</template></p>
          <p v-if="preview.polling.value" class="mt-2 text-small text-fg-2">{{ SOURCE.previewing }}</p>
          <InlineError v-if="previewProblem" :message="previewProblem" class="mt-2" />
          <InlineError
            v-if="preview.job.value && preview.job.value.state !== 'succeeded' && preview.job.value.lastError"
            :message="jobMessage(preview.job.value.lastError)" class="mt-2"
          />
          <PreviewResult v-if="previewResult && preview.job.value" :job-id="preview.job.value.id" :result="previewResult" class="mt-3" />
        </section>
      </template>
      <InlineError v-if="form.formError.value" :message="form.formError.value" />
    </form>
    <template #footer>
      <UiButton variant="primary" type="submit" form="source-create" :loading="saving" :disabled="blocked" data-test="save">
        {{ type === 'upload' ? SOURCE.saveUpload : SOURCE.save }}
      </UiButton>
      <UiButton variant="quiet" @click="emit('update:open', false)">{{ COMMON.cancel }}</UiButton>
    </template>
  </UiDrawer>
</template>

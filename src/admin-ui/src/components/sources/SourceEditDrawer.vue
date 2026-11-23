<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { Collection, Level, Source, SourceImpact } from '@/api/types'
import FetchModeFields from '@/components/sources/FetchModeFields.vue'
import ScheduleField from '@/components/sources/ScheduleField.vue'
import { type FetchMode, READY_SELECTOR_MAX, fetchForm, withFetchSettings } from '@/components/sources/fetchMode'
import { type SchedulePreset, scheduleForm, scheduleShapeValid, scheduleValue } from '@/components/sources/schedule'
import { sitemapsValid } from '@/components/sources/sitemaps'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { COMMON, LEVEL_LABEL, SOURCE, SOURCE_EDIT } from '@/utils/labels'

// Ändra källa (admin design §5.5.3, V25): name, level, addresses and scope, Fetch pages with (and for a browser its ready
// element and scrolling), Nedprioritera (demote patterns with a factor) and the personal-data mode. Saving first asks
// POST /api/sources/{id}/impact; a scope or identity change shows what it does (documents may leave the index, the
// assistants that use the collection) and saves only with Spara ändå, sending the impact token; a fetch change
// (refetch: every page fetched again in the new way) is shown the same way and needs no token. Fetch again sets the
// source's own schedule (empty: the platform default, engine design §9.6); a schedule has no impact. Settings the form does
// not show are kept as they are.
const props = defineProps<{ open: boolean; source: Source; collection: Collection | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'saved', source: Source): void }>()

const LEVELS: Level[] = ['public', 'internal', 'sensitive']
const name = ref('')
const level = ref<Level>('public')
const urls = ref('')
const include = ref('')
// #801: sitemaps besides robots.txt and /sitemap.xml (a scope change: shown with its impact).
const sitemaps = ref('')
const exclude = ref('')
const demote = ref('')
const maxPages = ref('')
const maxDepth = ref('')
const piiMode = ref<'hold' | 'report'>('hold')
const fetchMode = ref<FetchMode>('http')
const readySelector = ref('')
const scrollToLoad = ref(false)
const schedulePreset = ref<SchedulePreset>('default')
const scheduleCustom = ref('')
const impact = ref<SourceImpact | null>(null)
const saving = ref(false)
const form = useFormErrors({
  name: 'edit-name', urls: 'edit-urls', sitemaps: 'edit-sitemaps', demote: 'edit-demote', maxPages: 'edit-max-pages', maxDepth: 'edit-max-depth', readySelector: 'edit-ready-selector', schedule: 'edit-schedule-custom',
})

const crawl = computed(() => props.source.type === 'crawl')
const levelOptions = computed(() => {
  const ceiling = props.collection?.classification ?? 'sensitive'
  return LEVELS.slice(0, LEVELS.indexOf(ceiling) + 1).map((l) => ({ value: l, label: LEVEL_LABEL[l] }))
})
const usedByNames = computed(() =>
  (impact.value?.usedBy ?? [])
    .map((a) => (a.state === 'archived' ? `${a.name} (${SOURCE_EDIT.archived})` : a.published ? a.name : `${a.name} (${SOURCE_EDIT.draft})`))
    .join(', '),
)
const piiOptions = (['hold', 'report'] as const).map((m) => ({ value: m, label: SOURCE.piiModes[m] }))
const lines = (text: string) => text.split('\n').map((l) => l.trim()).filter(Boolean)
const strings = (value: unknown) => (Array.isArray(value) ? value.map(String) : [])

watch(
  () => props.open,
  (open) => {
    if (!open) return
    const s = props.source.settings
    name.value = props.source.name
    level.value = props.source.classification
    urls.value = strings(crawl.value ? s.startUrls : s.urls).join('\n')
    include.value = strings(s.include).join('\n')
    sitemaps.value = strings(s.sitemaps).join('\n')
    exclude.value = strings(s.exclude).join('\n')
    demote.value = (Array.isArray(s.demote) ? (s.demote as { pattern: string; factor?: number }[]) : [])
      .map((d) => `${d.pattern} ${String(d.factor ?? 0.5).replace('.', ',')}`).join('\n')
    maxPages.value = String(s.maxPages ?? 10000)
    maxDepth.value = String(s.maxDepth ?? 10)
    piiMode.value = s.piiMode === 'report' ? 'report' : 'hold'
    const fetch = fetchForm(s)
    fetchMode.value = fetch.fetchMode
    readySelector.value = fetch.readySelector
    scrollToLoad.value = fetch.scrollToLoad
    const schedule = scheduleForm(props.source.schedule)
    schedulePreset.value = schedule.preset
    scheduleCustom.value = schedule.custom
    impact.value = null
    form.clear()
  },
)
// A changed field makes a shown impact stale.
watch([name, level, urls, sitemaps, include, exclude, demote, maxPages, maxDepth, piiMode, fetchMode, readySelector, scrollToLoad], () => {
  impact.value = null
})

/** Nedprioritera: one pattern per line, optionally followed by a factor between 0 and 1 (default 0,5). */
function demoteRules(): { pattern: string; factor: number }[] | null {
  const rules: { pattern: string; factor: number }[] = []
  for (const line of lines(demote.value)) {
    const match = /^(\S+)(?:\s+(0(?:[.,]\d+)?|1(?:[.,]0+)?))?$/.exec(line)
    if (!match) return null
    const factor = match[2] ? Number(match[2].replace(',', '.')) : 0.5
    if (factor <= 0 || factor >= 1) return null
    rules.push({ pattern: match[1]!, factor })
  }
  return rules
}

function settings(): Record<string, unknown> {
  const base = withFetchSettings(props.source.settings, { fetchMode: fetchMode.value, readySelector: readySelector.value, scrollToLoad: scrollToLoad.value })
  if (props.collection?.classification === 'public') base.piiMode = piiMode.value
  if (!crawl.value) return { ...base, urls: lines(urls.value) }
  return {
    ...base, startUrls: lines(urls.value), sitemaps: lines(sitemaps.value), include: lines(include.value), exclude: lines(exclude.value), demote: demoteRules() ?? [],
    maxPages: Number(maxPages.value), maxDepth: Number(maxDepth.value),
  }
}

function valid(): boolean {
  const addresses = lines(urls.value)
  const integer = (v: string) => /^\d+$/.test(v.trim())
  return form.validate({
    name: !name.value.trim() && SOURCE.nameRequired,
    urls: (addresses.length === 0 || addresses.some((u) => !/^https?:\/\//.test(u))) && SOURCE.urlsRequired,
    sitemaps: crawl.value && !sitemapsValid(lines(sitemaps.value)) && SOURCE.sitemapsInvalid,
    demote: crawl.value && demoteRules() === null && SOURCE_EDIT.demoteInvalid,
    maxPages: crawl.value && !integer(maxPages.value) && SOURCE.numberInvalid,
    maxDepth: crawl.value && !integer(maxDepth.value) && SOURCE.numberInvalid,
    readySelector: fetchMode.value === 'browser' && readySelector.value.trim().length > READY_SELECTOR_MAX && SOURCE.readySelectorInvalid,
    schedule: !scheduleShapeValid(schedulePreset.value, scheduleCustom.value) && SOURCE.scheduleInvalid,
  })
}

async function put(token: string | null): Promise<void> {
  const saved = await api.sources.update(props.source.id, {
    name: name.value.trim(), classification: level.value, settings: settings(), schedule: scheduleValue(schedulePreset.value, scheduleCustom.value), version: props.source.version,
    ...(token ? { impactToken: token } : {}),
  })
  emit('saved', saved)
  emit('update:open', false)
}

async function save(): Promise<void> {
  if (!valid()) return
  saving.value = true
  try {
    if (impact.value) {
      await put(impact.value.impactToken)
      return
    }
    const checked = await api.sources.impact(props.source.id, { settings: settings() })
    if (checked.impactToken || checked.effects.includes('refetch')) impact.value = checked
    else await put(null)
  } catch (e) {
    form.applyServerError(e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UiDrawer :open="open" :title="SOURCE_EDIT.title" :subtitle="source.name" wide @update:open="emit('update:open', $event)">
    <form id="source-edit" class="grid gap-5" novalidate @submit.prevent="save">
      <TextField id="edit-name" v-model="name" :label="SOURCE.name" :error="form.errors.name" autocomplete="off" />
      <SelectField id="edit-level" v-model="level" :label="SOURCE.level" :hint="SOURCE.levelHint" :options="levelOptions" />
      <TextAreaField
        id="edit-urls" v-model="urls" :label="crawl ? SOURCE.startUrls : SOURCE.urls" :hint="crawl ? SOURCE.startUrlsHint : SOURCE.urlsHint"
        :error="form.errors.urls" mono
      />
      <FetchModeFields
        v-model:fetch-mode="fetchMode" v-model:ready-selector="readySelector" v-model:scroll-to-load="scrollToLoad" id-prefix="edit"
        :ready-selector-error="form.errors.readySelector"
      />
      <ScheduleField
        v-model:preset="schedulePreset" v-model:custom="scheduleCustom" id-prefix="edit"
        :default-schedule="source.schedule ? null : source.scheduleInForce" :error="form.errors.schedule"
      />
      <template v-if="crawl">
        <TextAreaField id="edit-sitemaps" v-model="sitemaps" :label="SOURCE.sitemaps" :hint="SOURCE.sitemapsHint" :error="form.errors.sitemaps" mono />
        <TextAreaField id="edit-include" v-model="include" :label="SOURCE.include" :hint="SOURCE.includeHint" mono />
        <TextAreaField id="edit-exclude" v-model="exclude" :label="SOURCE.exclude" :hint="SOURCE.excludeHint" mono />
        <TextAreaField id="edit-demote" v-model="demote" :label="SOURCE_EDIT.demote" :hint="SOURCE_EDIT.demoteHint" :error="form.errors.demote" mono />
        <TextField id="edit-max-pages" v-model="maxPages" :label="SOURCE.maxPages" inputmode="numeric" short :error="form.errors.maxPages" />
        <TextField id="edit-max-depth" v-model="maxDepth" :label="SOURCE.maxDepth" inputmode="numeric" short :error="form.errors.maxDepth" />
      </template>
      <SelectField v-if="collection?.classification === 'public'" id="edit-pii" v-model="piiMode" :label="SOURCE.piiMode" :options="piiOptions" />

      <div v-if="impact" role="alert" class="rounded-card border border-warn p-4" data-test="impact">
        <p class="font-medium">{{ SOURCE_EDIT.impactTitle }}</p>
        <p v-if="impact.effects.includes('scope')" class="mt-1">{{ SOURCE_EDIT.scope(impact.liveDocuments) }}</p>
        <p v-if="impact.effects.includes('identity')" class="mt-1">{{ SOURCE_EDIT.identity }}</p>
        <p v-if="impact.effects.includes('refetch')" class="mt-1" data-test="impact-refetch">{{ SOURCE_EDIT.refetch }}</p>
        <p v-if="impact.usedBy.length" class="mt-1">
          {{ SOURCE_EDIT.usedBy(usedByNames) }}
        </p>
      </div>
      <InlineError v-if="form.formError.value" :message="form.formError.value" />
    </form>
    <template #footer>
      <UiButton variant="primary" type="submit" form="source-edit" :loading="saving" data-test="save-source">
        {{ impact ? SOURCE_EDIT.saveAnyway : SOURCE_EDIT.save }}
      </UiButton>
      <UiButton variant="quiet" @click="emit('update:open', false)">{{ COMMON.cancel }}</UiButton>
    </template>
  </UiDrawer>
</template>

<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { api } from '@/api'
import type { PathRuleJson, TopicRulePreview } from '@/api/types'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { formatNumber } from '@/utils/format'
import { COMMON, TOPICS } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import { IGNORE_CASE, MEDIA_TYPES, escapeRegex, normaliseTag, plainText, ruleForm, tagError, tagsOf, type RuleForm } from '@/utils/topics'

// Add topic / Edit (admin design §5.5.2; Decisions "Topics editor"): *Topic*; *Documents* as one of "under the address",
// "whose title contains" (written as `title-regex` with the text escaped), "of the file type" (`mediaType`); *Source*
// (only with more than one); *Advanced* with every rule kind of processing §7, and a rule the form can't show in words as
// its JSON. Under it, "Matches N of M documents" with up to ten (`POST …/topic-rules/preview`, debounced, once the rule
// is complete). A rule that matches nothing says so and is asked again at Save, which still saves it. The drawer stays
// open until the parent has its plan under way: `saving` while it is made, `saveError` when it was refused (#1076).
const props = defineProps<{
  open: boolean
  collectionId: string
  sources: { id: string; name: string }[]
  /** Edit: the rule as stored, its source and the topic it sets; Add when absent. */
  initial?: { sourceId: string; rule: PathRuleJson; tag: string } | null
  saving?: boolean
  saveError?: string | null
}>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'save', value: { sourceId: string; rule: PathRuleJson }): void }>()

const form = reactive({
  tag: '', sourceId: '', kind: 'under' as RuleForm, address: '', title: '', type: 'application/pdf',
  on: 'path' as PathRuleJson['on'], match: '', documentType: '', date: '', json: '',
})
const tried = ref(false)
const preview = ref<TopicRulePreview | null>(null)
const previewError = ref<string | null>(null)
const previewing = ref(false)
const zeroOpen = ref(false)
let timer: ReturnType<typeof setTimeout> | null = null
let generation = 0
/** The preview asked for last: a newer one aborts it, so typing doesn't pile up requests on the server (#1076). */
let inFlight: AbortController | null = null

const kinds = computed(() => (form.kind === 'json' ? ['json'] : ['under', 'title', 'type', 'advanced']).map((k) => ({ value: k, label: TOPICS.kinds[k] ?? TOPICS.json })))
const typeOptions = Object.entries(MEDIA_TYPES).map(([value, label]) => ({ value, label: `${label} (${value})` }))
const onOptions = Object.entries(TOPICS.onOptions).map(([value, label]) => ({ value, label }))
const sourceName = computed(() => props.sources.find((s) => s.id === form.sourceId)?.name ?? '')

watch(
  () => props.open,
  (open) => {
    if (!open) return
    const initial = props.initial
    const rule = initial?.rule
    Object.assign(form, {
      tag: initial?.tag ?? '', sourceId: initial?.sourceId ?? props.sources[0]?.id ?? '', kind: rule ? ruleForm(rule) : 'under',
      address: '', title: '', type: 'application/pdf', on: 'path', match: '', documentType: '', date: '', json: '',
    })
    if (rule) {
      if (form.kind === 'under') form.address = rule.match
      else if (form.kind === 'title') form.title = plainText(rule.match) ?? ''
      else if (form.kind === 'type') form.type = rule.match.toLowerCase()
      else if (form.kind === 'advanced') Object.assign(form, { on: rule.on, match: rule.match, documentType: rule.documentType ?? '', date: rule.date ?? '' })
      else form.json = JSON.stringify(rule, null, 2)
    }
    tried.value = false
    preview.value = null
    previewError.value = null
  },
  { immediate: true },
)

// The knowledge base's sources can arrive after the form opened (the page loads them beside the topics): a form without
// one of them takes the first.
watch(
  () => props.sources.map((s) => s.id).join(),
  () => {
    if (!props.sources.some((s) => s.id === form.sourceId)) form.sourceId = props.sources[0]?.id ?? ''
  },
)

/** The rule the form describes, or null while it is incomplete (or its JSON is not one rule). */
const rule = computed<PathRuleJson | null>(() => {
  const tags = [normaliseTag(form.tag)]
  switch (form.kind) {
    case 'under':
      return form.address.trim() ? { match: form.address.trim(), on: 'path', tags } : null
    case 'title':
      // Case-insensitive: "protokoll" finds "Protokoll 2024".
      return form.title.trim() ? { match: IGNORE_CASE + escapeRegex(form.title.trim()), on: 'title-regex', tags } : null
    case 'type':
      return { match: form.type, on: 'mediaType', tags }
    case 'advanced':
      return form.match.trim()
        ? { match: form.match.trim(), on: form.on, tags, ...(form.documentType.trim() ? { documentType: form.documentType.trim() } : {}), ...(form.date.trim() ? { date: form.date.trim() } : {}) }
        : null
    default:
      try {
        const parsed = JSON.parse(form.json) as PathRuleJson
        return parsed && typeof parsed === 'object' && typeof parsed.match === 'string' && typeof parsed.on === 'string'
          && (parsed.tags === undefined || (Array.isArray(parsed.tags) && parsed.tags.every((t) => typeof t === 'string'))) ? parsed : null
      } catch {
        return null
      }
  }
})

/** The topic's check; in JSON, every tag the rule sets, and at least one (a rule without topics isn't one on this tab). */
const tagProblem = computed(() => {
  if (form.kind !== 'json') return tagError(form.tag)
  if (!rule.value) return null
  const tags = tagsOf(rule.value)
  return tags.length === 0 ? TOPICS.tagsRequired : tags.map(tagError).find((e) => e !== null) ?? null
})
const ruleProblem = computed(() => (rule.value ? null : form.kind === 'json' ? TOPICS.jsonInvalid : TOPICS.matchRequired))

async function runPreview(): Promise<TopicRulePreview | null> {
  const current = rule.value
  if (!current || !form.sourceId) return null
  const mine = ++generation
  inFlight?.abort()
  const controller = new AbortController()
  inFlight = controller
  previewing.value = true
  previewError.value = null
  try {
    // The preview needs a valid tag shape; the topic itself does not change what matches.
    const tags = tagsOf(current)
    const answer = await api.collections.previewTopicRule(props.collectionId, { sourceId: form.sourceId, rule: { ...current, tags: tags.length && tags.every((t) => tagError(t) === null) ? tags : ['preview'] } }, controller.signal)
    if (mine === generation) preview.value = answer
    return answer
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') return null
    if (mine === generation) {
      preview.value = null
      previewError.value = problemMessage(e)
    }
    return null
  } finally {
    if (mine === generation) previewing.value = false
  }
}

// Debounced while typing; only for a complete rule on a source.
watch(
  () => [JSON.stringify({ ...rule.value, tags: [] }), form.sourceId, props.open],
  () => {
    if (timer) clearTimeout(timer)
    preview.value = null
    previewError.value = null
    if (!props.open || !rule.value) return
    timer = setTimeout(() => void runPreview(), 400)
  },
)

async function save(): Promise<void> {
  tried.value = true
  if (tagProblem.value || !rule.value) return
  if (timer) clearTimeout(timer)
  const answer = preview.value ?? (await runPreview())
  if (previewError.value) return
  if (answer && answer.matched === 0 && !answer.partial) {
    zeroOpen.value = true
    return
  }
  emitSave()
}

/** The parent plans the change and closes the drawer once the plan is under way; a refusal shows here (`saveError`). */
function emitSave(): void {
  emit('save', { sourceId: form.sourceId, rule: rule.value! })
}
</script>

<template>
  <UiDrawer :open="open" :title="initial ? TOPICS.editTopic : TOPICS.addTopic" wide @update:open="emit('update:open', $event)">
    <form id="topic-form" class="grid gap-5" novalidate data-test="topic-form" @submit.prevent="save">
      <TextField
        v-if="form.kind !== 'json'" id="topic-tag" v-model="form.tag" :label="TOPICS.topic" mono autocomplete="off"
        :error="tried || form.tag ? tagProblem : null" data-test="topic-form-tag"
      />
      <SelectField
        v-if="sources.length > 1" id="topic-source" v-model="form.sourceId" :label="TOPICS.sourceField"
        :options="sources.map((s) => ({ value: s.id, label: s.name }))" data-test="topic-form-source"
      />
      <fieldset class="grid gap-3">
        <legend class="mb-2 font-medium">{{ TOPICS.documentsField }}</legend>
        <div class="flex flex-wrap gap-x-5 gap-y-2">
          <label v-for="k in kinds" :key="k.value" class="flex cursor-pointer items-center gap-2">
            <input v-model="form.kind" type="radio" name="topic-kind" :value="k.value" :data-test="`topic-kind-${k.value}`" />
            {{ k.label }}
          </label>
        </div>
        <TextField
          v-if="form.kind === 'under'" id="topic-address" v-model="form.address" :label="TOPICS.address" :hint="TOPICS.addressHint" mono
          :error="tried ? ruleProblem : null" data-test="topic-form-address"
        />
        <TextField
          v-else-if="form.kind === 'title'" id="topic-title" v-model="form.title" :label="TOPICS.titleText" :error="tried ? ruleProblem : null"
          data-test="topic-form-title"
        />
        <SelectField v-else-if="form.kind === 'type'" id="topic-type" v-model="form.type" :label="TOPICS.fileType" :options="typeOptions" data-test="topic-form-type" />
        <template v-else-if="form.kind === 'advanced'">
          <p class="m-0 text-small text-fg-2">{{ TOPICS.advancedLead }}</p>
          <SelectField id="topic-on" v-model="form.on" :label="TOPICS.on" :options="onOptions" data-test="topic-form-on" />
          <TextField id="topic-match" v-model="form.match" :label="TOPICS.match" mono :error="tried ? ruleProblem : null" data-test="topic-form-match" />
          <TextField id="topic-document-type" v-model="form.documentType" :label="TOPICS.documentType" data-test="topic-form-document-type" />
          <TextField id="topic-date" v-model="form.date" :label="TOPICS.date" :hint="TOPICS.dateHint" mono data-test="topic-form-date" />
        </template>
        <TextAreaField
          v-else id="topic-json" v-model="form.json" :label="TOPICS.json" :hint="TOPICS.jsonHint" mono :error="tried || form.json ? ruleProblem ?? tagProblem : null"
          data-test="topic-form-json"
        />
      </fieldset>

      <InlineError v-if="saveError" :message="saveError" data-test="topic-form-error" />
      <section class="grid gap-2 rounded-card bg-sunken p-4" aria-live="polite" data-test="topic-preview">
        <p v-if="previewing" class="m-0 text-fg-2">{{ TOPICS.previewing }}</p>
        <InlineError v-else-if="previewError" :message="previewError" data-test="topic-preview-error" />
        <template v-else-if="preview">
          <p class="m-0 font-medium" data-test="topic-preview-count">
            {{ preview.partial ? TOPICS.matchesPartial(formatNumber(preview.matched), formatNumber(preview.checked), formatNumber(preview.total))
              : TOPICS.matches(formatNumber(preview.matched), formatNumber(preview.total)) }}
          </p>
          <p v-if="preview.matched === 0 && !preview.partial" class="m-0 text-warn" data-test="topic-preview-none">{{ TOPICS.noMatch(sourceName) }}</p>
          <template v-else>
            <p class="m-0 text-small text-fg-2">{{ TOPICS.firstTen }}</p>
            <ul class="m-0 grid gap-1 pl-5 text-small">
              <li v-for="d in preview.sample" :key="d.documentId" data-test="topic-preview-item">
                {{ d.title ?? d.displayName ?? d.url }}<span v-if="d.url ?? d.displayName" class="ml-2 font-mono text-fg-3">{{ d.url ?? d.displayName }}</span>
              </li>
            </ul>
          </template>
        </template>
      </section>
    </form>
    <template #footer>
      <UiButton variant="primary" type="submit" form="topic-form" :loading="(previewing && tried) || saving" data-test="topic-form-save">{{ TOPICS.save }}</UiButton>
      <UiButton variant="quiet" @click="emit('update:open', false)">{{ COMMON.cancel }}</UiButton>
    </template>
    <ConfirmDialog
      v-model:open="zeroOpen" :title="TOPICS.zeroTitle" :consequence="TOPICS.zeroConsequence" :confirm-label="TOPICS.zeroConfirm" :action="emitSave"
    />
  </UiDrawer>
</template>

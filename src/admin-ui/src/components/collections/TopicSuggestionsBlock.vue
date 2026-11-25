<script setup lang="ts">
import { computed, reactive, ref, useId, watch } from 'vue'
import { api } from '@/api'
import type { SourceTopicSuggestions, TopicSection } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import { formatNumber } from '@/utils/format'
import { TOPICS } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import { tagError } from '@/utils/topics'

// One source's *Suggested from {source}* (admin design §5.5.2; Decisions "Topics editor"): the sections with their
// counts and titles. A section a topic covers is ticked with its topic and can't be unticked here. Ticking proposes the
// tag, editable in place and checked as typed. *show the sections under it* loads one section's next level (`under=`).
// The picks are the parent's: path → tag. A reload of the block (after a save) closes the open levels, so none shows
// sections as they were before the save (#1075); closing a level drops the picks under it, which no longer show.
const props = defineProps<{ collectionId: string; block: SourceTopicSuggestions; picks: Record<string, string> }>()
const emit = defineEmits<{ (e: 'pick', path: string, tag: string | null): void }>()
const id = useId()

/** A section's next level once asked for: loading, loaded (possibly empty) or failed. */
const under = reactive<Record<string, { loading: boolean; sections: TopicSection[]; error: unknown; open: boolean }>>({})

watch(() => props.block, (block) => {
  for (const path of Object.keys(under)) delete under[path]
  // The levels are closed: a pick under one would count in Add N with nothing on screen.
  const shown = new Set(block.sections.map((s) => s.path))
  for (const picked of Object.keys(props.picks)) if (!shown.has(picked)) emit('pick', picked, null)
})

interface Row { section: TopicSection; depth: number; parent: TopicSection | null }

/** The sections in order, each opened one followed by its next level (indented). */
const rows = computed<Row[]>(() => {
  const out: Row[] = []
  const walk = (sections: TopicSection[], depth: number, parent: TopicSection | null) => {
    for (const section of sections) {
      out.push({ section, depth, parent })
      const next = under[section.path]
      if (next?.open) walk(next.sections, depth + 1, section)
    }
  }
  walk(props.block.sections, 0, null)
  return out
})

/** The level last loaded with nothing in it, for the status line. */
const emptyUnder = ref<string | null>(null)
/** What a screen reader hears about the levels: one loading, or one with nothing under it. */
const status = computed(() => {
  const loading = Object.entries(under).find(([, u]) => u.open && u.loading)
  if (loading) return TOPICS.loadingUnder(loading[0])
  return emptyUnder.value && under[emptyUnder.value]?.open ? TOPICS.noneUnder : ''
})

async function toggle(path: string): Promise<void> {
  const next = under[path]
  if (next?.open) {
    next.open = false
    // The picks under it would still count in Add N topics with nothing on screen.
    for (const picked of Object.keys(props.picks)) if (picked !== path && picked.startsWith(path)) emit('pick', picked, null)
    return
  }
  if (next && !next.error) {
    next.open = true
    return
  }
  under[path] = { loading: true, sections: [], error: null, open: true }
  const entry = under[path]! // the reactive entry, as later reads give it
  // A reload of the block may drop this level while its request runs: the answer then goes nowhere.
  const current = () => under[path] === entry
  try {
    const answer = await api.collections.topicSuggestions(props.collectionId, { sourceId: props.block.sourceId, under: path })
    if (!current()) return
    entry.sections = answer.sources[0]?.sections ?? []
    emptyUnder.value = entry.sections.length === 0 ? path : null
  } catch (e) {
    if (current()) entry.error = e
  } finally {
    if (current()) entry.loading = false
  }
}

function picked(path: string): boolean {
  return props.picks[path] !== undefined
}

function tick(section: TopicSection, on: boolean): void {
  emit('pick', section.path, on ? (section.suggestedTag ?? '') : null)
}

const label = (s: TopicSection) => s.title ?? s.path
/** A section's checkbox name, with the section it is under (the levels are shown by indent only). */
const pickName = (row: Row) => (row.parent ? TOPICS.pickUnder(label(row.section), label(row.parent)) : TOPICS.pick(label(row.section)))
</script>

<template>
  <section class="grid gap-2" :aria-labelledby="`suggested-${id}`" data-test="topic-suggestions">
    <h3 :id="`suggested-${id}`" class="text-body font-medium">{{ TOPICS.suggested(block.sourceName) }}</h3>
    <p class="m-0 text-small text-fg-2">{{ TOPICS.suggestedLead }}</p>
    <p class="sr-only" role="status" data-test="suggestions-status">{{ status }}</p>
    <p v-if="block.sections.length === 0" class="m-0 text-fg-3" data-test="no-suggestions">{{ TOPICS.noSuggestions }}</p>
    <div v-else class="overflow-x-auto">
      <table class="w-full text-left">
        <caption class="sr-only">{{ TOPICS.suggested(block.sourceName) }}</caption>
        <thead class="text-small text-fg-2">
          <tr>
            <th scope="col" class="py-2 pr-3 font-medium">{{ TOPICS.section }}</th>
            <th scope="col" class="py-2 pr-3 font-medium">{{ TOPICS.tag }}</th>
            <th scope="col" class="py-2 text-right font-medium">{{ TOPICS.documents }}</th>
          </tr>
        </thead>
        <tbody>
          <template v-for="(row, i) in rows" :key="row.section.path">
            <tr class="border-t border-line align-top" :data-path="row.section.path" data-test="topic-section">
              <td class="py-2 pr-3" :style="{ paddingLeft: `${row.depth * 1.5}rem` }">
                <label class="flex cursor-pointer items-start gap-3">
                  <input
                    type="checkbox" class="mt-1"
                    :checked="row.section.coveredBy.length > 0 || picked(row.section.path)"
                    :disabled="row.section.coveredBy.length > 0"
                    :aria-label="pickName(row)"
                    data-test="topic-pick"
                    @change="tick(row.section, ($event.target as HTMLInputElement).checked)"
                  />
                  <span class="grid">
                    <span class="font-medium">{{ label(row.section) }}</span>
                    <span v-if="row.section.title" class="font-mono text-small text-fg-3">{{ row.section.path }}</span>
                  </span>
                </label>
                <button
                  type="button" class="ml-7 inline-flex min-h-6 items-center text-small text-accent-ink underline max-md:min-h-11" :aria-expanded="under[row.section.path]?.open ? 'true' : 'false'"
                  data-test="show-under" @click="toggle(row.section.path)"
                >
                  {{ under[row.section.path]?.open ? TOPICS.hideUnder : TOPICS.showUnder }}
                </button>
              </td>
              <td class="py-2 pr-3">
                <span v-if="row.section.coveredBy.length" class="text-fg-2" data-test="topic-covered">{{ TOPICS.covered(row.section.coveredBy.join(', ')) }}</span>
                <template v-else-if="picked(row.section.path)">
                  <input
                    :value="picks[row.section.path]"
                    class="field-input max-w-64 font-mono"
                    autocomplete="off"
                    :aria-label="TOPICS.tagFor(label(row.section))"
                    :aria-invalid="tagError(picks[row.section.path]!) ? 'true' : undefined"
                    :aria-describedby="tagError(picks[row.section.path]!) ? `tag-error-${id}-${i}` : undefined"
                    data-test="topic-tag"
                    @input="emit('pick', row.section.path, ($event.target as HTMLInputElement).value)"
                  />
                  <p v-if="tagError(picks[row.section.path]!)" :id="`tag-error-${id}-${i}`" class="m-0 mt-1 text-small text-danger" data-test="topic-tag-error">
                    {{ tagError(picks[row.section.path]!) }}
                  </p>
                </template>
              </td>
              <td class="py-2 text-right tabular">{{ formatNumber(row.section.documents) }}</td>
            </tr>
            <tr v-if="under[row.section.path]?.open && (under[row.section.path]!.loading || under[row.section.path]!.error || under[row.section.path]!.sections.length === 0)">
              <td colspan="3" class="pb-2 text-small" :style="{ paddingLeft: `${(row.depth + 1) * 1.5}rem` }">
                <span v-if="under[row.section.path]!.loading" class="text-fg-2" data-test="loading-under">{{ TOPICS.loadingUnder(label(row.section)) }}</span>
                <InlineError v-else-if="under[row.section.path]!.error" :message="problemMessage(under[row.section.path]!.error)" />
                <span v-else class="text-fg-3" data-test="none-under">{{ TOPICS.noneUnder }}</span>
              </td>
            </tr>
          </template>
        </tbody>
      </table>
    </div>
  </section>
</template>

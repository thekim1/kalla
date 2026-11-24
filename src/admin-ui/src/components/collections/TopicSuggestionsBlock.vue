<script setup lang="ts">
import { computed, reactive, useId } from 'vue'
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
// The picks are the parent's: path → tag.
const props = defineProps<{ collectionId: string; block: SourceTopicSuggestions; picks: Record<string, string> }>()
const emit = defineEmits<{ (e: 'pick', path: string, tag: string | null): void }>()
const id = useId()

/** A section's next level once asked for: loading, loaded (possibly empty) or failed. */
const under = reactive<Record<string, { loading: boolean; sections: TopicSection[]; error: unknown; open: boolean }>>({})

interface Row { section: TopicSection; depth: number }

/** The sections in order, each opened one followed by its next level (indented). */
const rows = computed<Row[]>(() => {
  const out: Row[] = []
  const walk = (sections: TopicSection[], depth: number) => {
    for (const section of sections) {
      out.push({ section, depth })
      const next = under[section.path]
      if (next?.open) walk(next.sections, depth + 1)
    }
  }
  walk(props.block.sections, 0)
  return out
})

async function toggle(path: string): Promise<void> {
  const next = under[path]
  if (next) {
    next.open = !next.open
    if (!next.error) return
  }
  under[path] = { loading: true, sections: [], error: null, open: true }
  try {
    const answer = await api.collections.topicSuggestions(props.collectionId, { sourceId: props.block.sourceId, under: path })
    under[path]!.sections = answer.sources[0]?.sections ?? []
  } catch (e) {
    under[path]!.error = e
  } finally {
    under[path]!.loading = false
  }
}

function picked(path: string): boolean {
  return props.picks[path] !== undefined
}

function tick(section: TopicSection, on: boolean): void {
  emit('pick', section.path, on ? (section.suggestedTag ?? '') : null)
}

const label = (s: TopicSection) => s.title ?? s.path
</script>

<template>
  <section class="grid gap-2" :aria-labelledby="`suggested-${id}`" data-test="topic-suggestions">
    <h3 :id="`suggested-${id}`" class="text-body font-medium">{{ TOPICS.suggested(block.sourceName) }}</h3>
    <p class="m-0 text-small text-fg-2">{{ TOPICS.suggestedLead }}</p>
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
          <template v-for="({ section, depth }, i) in rows" :key="section.path">
            <tr class="border-t border-line align-top" :data-path="section.path" data-test="topic-section">
              <td class="py-2 pr-3" :style="{ paddingLeft: `${depth * 1.5}rem` }">
                <label class="flex cursor-pointer items-start gap-3">
                  <input
                    type="checkbox" class="mt-1"
                    :checked="section.coveredBy.length > 0 || picked(section.path)"
                    :disabled="section.coveredBy.length > 0"
                    :aria-label="TOPICS.pick(label(section))"
                    data-test="topic-pick"
                    @change="tick(section, ($event.target as HTMLInputElement).checked)"
                  />
                  <span class="grid">
                    <span class="font-medium">{{ label(section) }}</span>
                    <span v-if="section.title" class="font-mono text-small text-fg-3">{{ section.path }}</span>
                  </span>
                </label>
                <button
                  type="button" class="ml-7 text-small text-accent-ink underline" :aria-expanded="under[section.path]?.open ? 'true' : 'false'"
                  data-test="show-under" @click="toggle(section.path)"
                >
                  {{ under[section.path]?.open ? TOPICS.hideUnder : TOPICS.showUnder }}
                </button>
              </td>
              <td class="py-2 pr-3">
                <span v-if="section.coveredBy.length" class="text-fg-2" data-test="topic-covered">{{ TOPICS.covered(section.coveredBy.join(', ')) }}</span>
                <template v-else-if="picked(section.path)">
                  <input
                    :value="picks[section.path]"
                    class="field-input max-w-64 font-mono"
                    autocomplete="off"
                    :aria-label="TOPICS.tagFor(label(section))"
                    :aria-invalid="tagError(picks[section.path]!) ? 'true' : undefined"
                    :aria-describedby="tagError(picks[section.path]!) ? `tag-error-${id}-${i}` : undefined"
                    data-test="topic-tag"
                    @input="emit('pick', section.path, ($event.target as HTMLInputElement).value)"
                  />
                  <p v-if="tagError(picks[section.path]!)" :id="`tag-error-${id}-${i}`" class="m-0 mt-1 text-small text-danger" data-test="topic-tag-error">
                    {{ tagError(picks[section.path]!) }}
                  </p>
                </template>
              </td>
              <td class="py-2 text-right tabular">{{ formatNumber(section.documents) }}</td>
            </tr>
            <tr v-if="under[section.path]?.open && !under[section.path]!.loading && (under[section.path]!.error || under[section.path]!.sections.length === 0)">
              <td colspan="3" class="pb-2 text-small" :style="{ paddingLeft: `${(depth + 1) * 1.5}rem` }">
                <InlineError v-if="under[section.path]!.error" :message="problemMessage(under[section.path]!.error)" />
                <span v-else class="text-fg-3" data-test="none-under">{{ TOPICS.noneUnder }}</span>
              </td>
            </tr>
          </template>
        </tbody>
      </table>
    </div>
  </section>
</template>

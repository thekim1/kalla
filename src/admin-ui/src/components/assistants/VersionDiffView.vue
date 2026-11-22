<script setup lang="ts">
import { computed } from 'vue'
import type { TextDiff, VersionDiff } from '@/api/types'
import { VERSION_DIFF as L } from '@/utils/labels'

// The structured diff of two versions (admin design §4.5, §5.5.4; B-56): settings by name, knowledge bases added or removed,
// and a line diff of the texts. Added and removed lines are <ins> and <del> with a sign, so the change is not told by colour alone.
const props = defineProps<{ diff: VersionDiff }>()

const texts = computed(() =>
  ([[L.instructions, props.diff.instructions], [L.notFoundSv, props.diff.notFoundSv], [L.notFoundEn, props.diff.notFoundEn]] as [string, TextDiff][])
    .filter(([, d]) => d.changed),
)
const knowledge = computed(() => [
  ...props.diff.collectionsAdded.map((c) => L.added(c.name ?? L.unknownCollection)),
  ...props.diff.collectionsRemoved.map((c) => L.removed(c.name ?? L.unknownCollection)),
  ...props.diff.topicsChanged.map((c) => L.topics(c.name ?? L.unknownCollection, topics(c.from), topics(c.to))),
])

function topics(tags: string[]): string {
  return tags.length ? tags.join(', ') : L.allTopics
}

function value(v: unknown): string {
  if (v === null || v === undefined) return L.none
  return typeof v === 'string' ? v : JSON.stringify(v)
}
</script>

<template>
  <div class="grid gap-6" data-test="version-diff">
    <p v-if="!diff.changed" data-test="diff-nothing">{{ L.nothing }}</p>
    <section v-if="diff.settings.length" data-test="diff-settings">
      <h3 class="mb-2 font-medium">{{ L.settings }}</h3>
      <table class="w-full text-small">
        <thead>
          <tr class="text-left text-fg-3"><th scope="col" class="pr-4">{{ L.setting }}</th><th scope="col" class="pr-4">{{ L.before }}</th><th scope="col">{{ L.after }}</th></tr>
        </thead>
        <tbody>
          <tr v-for="s in diff.settings" :key="s.name" class="align-top">
            <th scope="row" class="pr-4 text-left font-normal">{{ L.names[s.name] ?? s.name }}</th>
            <td class="pr-4 font-mono break-all">{{ value(s.from) }}</td>
            <td class="font-mono break-all">{{ value(s.to) }}</td>
          </tr>
        </tbody>
      </table>
    </section>
    <section v-if="knowledge.length" data-test="diff-knowledge">
      <h3 class="mb-2 font-medium">{{ L.knowledge }}</h3>
      <ul class="grid gap-1">
        <li v-for="line in knowledge" :key="line">{{ line }}</li>
      </ul>
    </section>
    <section v-for="[title, text] in texts" :key="title" :data-test="`diff-text-${title}`">
      <h3 class="mb-2 font-medium">{{ title }}</h3>
      <p v-if="text.tooLong" class="text-fg-2">{{ L.tooLong }}</p>
      <div v-else class="max-h-96 overflow-auto rounded-card border border-border p-3 font-mono text-small whitespace-pre-wrap">
        <template v-for="(line, i) in text.lines" :key="i">
          <ins v-if="line.op === 'insert'" class="block bg-ok-soft no-underline" :title="L.inserted"><span aria-hidden="true">+ </span><span class="sr-only">{{ L.inserted }}: </span>{{ line.text }}</ins>
          <del v-else-if="line.op === 'delete'" class="block bg-danger-soft" :title="L.deleted"><span aria-hidden="true">− </span><span class="sr-only">{{ L.deleted }}: </span>{{ line.text }}</del>
          <span v-else class="block text-fg-2"><span aria-hidden="true">&nbsp;&nbsp;</span>{{ line.text }}</span>
        </template>
      </div>
    </section>
  </div>
</template>

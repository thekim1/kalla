<script setup lang="ts">
import { computed, ref, useId } from 'vue'
import type { CollectionLink, CollectionSummary } from '@/api/types'
import UiBadge from '@/components/ui/UiBadge.vue'
import MenuButton from '@/components/ui/MenuButton.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import UiTabs from '@/components/ui/UiTabs.vue'
import { formatNumber } from '@/utils/format'
import { ASSISTANT, LEVEL_LABEL } from '@/utils/labels'

// Knowledge on the Build tab (admin design §5.5.4): one row per link with the knowledge base, its owning department when
// it is shared, and its topics ("all topics", or the count, with the topics as chips); Limit to topics opens the topic
// field ("Limit to topics (empty: all topics)", the knowledge base's topics as suggestions) and the row's ⋯ menu holds
// Remove. Add knowledge base opens a picker with Ours, Shared with us and All, showing level, document count and topics.
// "Ours" is the assistant's own department.
type View = 'mine' | 'shared' | 'all'
const props = defineProps<{ modelValue: CollectionLink[]; collections: CollectionSummary[]; departmentId: string; disabled?: boolean }>()
const emit = defineEmits<{ (e: 'update:modelValue', value: CollectionLink[]): void }>()
const uid = useId()
const picking = ref(false)
const view = ref<View>('mine')
const draftTopic = ref<Record<string, string>>({})
/** The links whose topic field is open (Limit to topics). */
const limiting = ref(new Set<string>())
function toggleLimit(id: string): void {
  const next = new Set(limiting.value)
  if (!next.delete(id)) next.add(id)
  limiting.value = next
}
const views = [
  { value: 'mine' as const, label: ASSISTANT.pickerViews.mine },
  { value: 'shared' as const, label: ASSISTANT.pickerViews.shared },
  { value: 'all' as const, label: ASSISTANT.pickerViews.all },
]
const byId = computed(() => new Map(props.collections.map((c) => [c.id, c])))
const sharedIn = (c: CollectionSummary | undefined) => !!c && c.departmentId !== props.departmentId
const candidates = computed(() =>
  props.collections
    .filter((c) => !props.modelValue.some((l) => l.collectionId === c.id))
    .filter((c) => view.value === 'all' || (view.value === 'shared') === sharedIn(c)),
)

/** Owner · level · documents (· not published), on one line. */
function details(c: CollectionSummary): string {
  return [c.departmentName, LEVEL_LABEL[c.classification], ASSISTANT.pickerDocuments(formatNumber(c.documentCount)), ...(c.publishedAt ? [] : [ASSISTANT.notPublished])].join(' · ')
}

function nameOf(id: string): string {
  return byId.value.get(id)?.name ?? id
}

function add(c: CollectionSummary): void {
  emit('update:modelValue', [...props.modelValue, { collectionId: c.id, tags: [] }])
  picking.value = false
}

function remove(id: string): void {
  emit('update:modelValue', props.modelValue.filter((l) => l.collectionId !== id))
}

function addTopic(id: string): void {
  const topic = (draftTopic.value[id] ?? '').trim()
  if (!topic) return
  emit('update:modelValue', props.modelValue.map((l) => (l.collectionId === id && !l.tags.includes(topic) ? { ...l, tags: [...l.tags, topic] } : l)))
  draftTopic.value = { ...draftTopic.value, [id]: '' }
}

function removeTopic(id: string, topic: string): void {
  emit('update:modelValue', props.modelValue.map((l) => (l.collectionId === id ? { ...l, tags: l.tags.filter((t) => t !== topic) } : l)))
}
</script>

<template>
  <fieldset class="grid gap-2" data-test="knowledge">
    <legend class="mb-1 font-medium">{{ ASSISTANT.collections }}</legend>
    <p class="text-small text-fg-2">{{ ASSISTANT.collectionsHint }}</p>
    <p v-if="modelValue.length === 0" class="text-fg-3">{{ ASSISTANT.noCollections }}</p>
    <ul v-else class="divide-y divide-border rounded-card border border-border">
      <li v-for="link in modelValue" :key="link.collectionId" class="grid gap-2 px-3 py-2.5" :data-test="`link-${link.collectionId}`">
        <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
          <span class="min-w-0 flex-1">
            <span class="font-medium">{{ nameOf(link.collectionId) }}</span>
            <span v-if="sharedIn(byId.get(link.collectionId))" class="text-fg-2"> ({{ ASSISTANT.sharedFrom(byId.get(link.collectionId)!.departmentName) }})</span>
            <span v-if="byId.get(link.collectionId) && !byId.get(link.collectionId)!.publishedAt" class="text-fg-3"> ({{ ASSISTANT.notPublished }})</span>
          </span>
          <span class="text-small text-fg-2" data-test="topics-summary">{{ link.tags.length ? ASSISTANT.topicCount(link.tags.length) : ASSISTANT.allTopicsShort }}</span>
          <template v-if="!disabled">
            <UiButton
              size="sm" :aria-expanded="limiting.has(link.collectionId) ? 'true' : 'false'" :aria-controls="`${uid}-limit-${link.collectionId}`"
              data-test="limit-topics" @click="toggleLimit(link.collectionId)"
            >
              {{ ASSISTANT.limitTopicsAction }}
            </UiButton>
            <MenuButton
              size="sm" :label="ASSISTANT.linkMenu(nameOf(link.collectionId))"
              :items="[{ key: 'remove', label: ASSISTANT.removeLink, icon: 'close', danger: true }]" @select="remove(link.collectionId)"
            />
          </template>
        </div>
        <div v-if="link.tags.length" class="flex flex-wrap items-center gap-2">
          <span v-for="t in link.tags" :key="t" class="inline-flex items-center gap-1 rounded-control bg-sunken px-2 py-0.5 text-small">
            <span class="font-mono">{{ t }}</span>
            <button
              v-if="!disabled" type="button" class="grid size-6 place-items-center rounded-control hover:bg-hover" :aria-label="ASSISTANT.removeTopic(t)"
              @click="removeTopic(link.collectionId, t)"
            >
              ×
            </button>
          </span>
        </div>
        <form
          v-if="!disabled && limiting.has(link.collectionId)" :id="`${uid}-limit-${link.collectionId}`" class="flex flex-wrap items-end gap-2"
          @submit.prevent="addTopic(link.collectionId)"
        >
          <label class="grid gap-1 text-small">
            <span>{{ ASSISTANT.limitTopics }}</span>
            <input
              v-model="draftTopic[link.collectionId]" :list="`${uid}-topics-${link.collectionId}`" class="field-input font-mono"
              autocomplete="off"
            />
          </label>
          <datalist :id="`${uid}-topics-${link.collectionId}`">
            <option v-for="t in (byId.get(link.collectionId)?.topics ?? []).filter((x) => !link.tags.includes(x))" :key="t" :value="t" />
          </datalist>
          <UiButton size="sm" type="submit">{{ ASSISTANT.addTopic }}</UiButton>
        </form>
      </li>
    </ul>
    <div v-if="!disabled"><UiButton icon="add" data-test="add-knowledge" @click="picking = true">{{ ASSISTANT.addCollection }}</UiButton></div>
  </fieldset>
  <UiDrawer v-model:open="picking" :title="ASSISTANT.pickerTitle">
    <UiTabs :id="`${uid}-picker`" v-model="view" :tabs="views" :label="ASSISTANT.pickerTitle" class="mb-4" />
    <div :id="`${uid}-picker-panel-${view}`" role="tabpanel" :aria-labelledby="`${uid}-picker-tab-${view}`">
      <p v-if="candidates.length === 0" class="text-fg-3">{{ ASSISTANT.pickerEmpty }}</p>
      <ul v-else class="grid gap-2" data-test="picker">
        <li v-for="c in candidates" :key="c.id" class="flex flex-wrap items-center justify-between gap-3 rounded-card border border-border p-3">
          <span class="grid gap-0.5">
            <span class="font-medium">{{ c.name }}</span>
            <span class="text-small text-fg-2">{{ details(c) }}</span>
            <span v-if="c.topics.length" class="text-small text-fg-3">{{ c.topics.join(', ') }}</span>
            <UiBadge v-if="sharedIn(c)" tone="accent" :label="ASSISTANT.pickerViews.shared" class="justify-self-start" />
          </span>
          <UiButton size="sm" variant="primary" :aria-label="ASSISTANT.pickerAdd(c.name)" @click="add(c)">{{ ASSISTANT.addCollection }}</UiButton>
        </li>
      </ul>
    </div>
  </UiDrawer>
</template>

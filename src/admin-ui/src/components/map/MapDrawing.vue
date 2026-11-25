<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useId, watch } from 'vue'
import type { MapRow } from '@/api/types'
import { MAP } from '@/utils/labels'
import { STAGES, connected, drawingOf, feeds, type MapNode } from '@/utils/mapDrawing'

// The Map as a drawing (admin design §5.5.6, B-67): one column per stage (sources → knowledge bases → assistants →
// channels), each item a link to its page. The lines are plain SVG measured from the items (no graph library, §10.2);
// every relation is also text under its item ("Feeds: …", "Used by: …"), so nothing depends on the drawing, and on a
// phone, where the columns stack, the lines are left out. Pointing at or focusing an item highlights what it feeds and
// what feeds it.
const props = defineProps<{ departmentName: string; rows: MapRow[] }>()

const id = useId()
const drawing = computed(() => drawingOf(props.rows))
const active = ref<string | null>(null)
const lit = computed(() => (active.value ? connected(drawing.value, active.value) : null))
const area = ref<HTMLElement | null>(null)
interface Line { key: string; d: string; draft: boolean; on: boolean }
const lines = ref<Omit<Line, 'on'>[]>([])
const shown = computed<Line[]>(() => lines.value.map((l) => {
  const [from, to] = l.key.split('>') as [string, string]
  return { ...l, on: !!lit.value && lit.value.has(from) && lit.value.has(to) }
}))

function relation(node: MapNode): string | null {
  const names = feeds(drawing.value, node.key)
  const say = MAP.drawing.relations[node.stage]
  return names.length && say ? say(names.join(', ')) : null
}

/** Each line from the right edge of what feeds to the left edge of what it feeds, as a gentle S. */
function measure(): void {
  const box = area.value
  if (!box) return
  const origin = box.getBoundingClientRect()
  const at = (key: string) => box.querySelector(`[data-node="${key}"]`)?.getBoundingClientRect()
  lines.value = drawing.value.edges.flatMap((e) => {
    const a = at(e.from)
    const b = at(e.to)
    if (!a || !b || !a.width || !b.width) return []
    const x1 = a.right - origin.left
    const y1 = a.top + a.height / 2 - origin.top
    const x2 = b.left - origin.left - 6
    const y2 = b.top + b.height / 2 - origin.top
    const mid = (x1 + x2) / 2
    return [{ key: `${e.from}>${e.to}`, d: `M${x1},${y1} C${mid},${y1} ${mid},${y2} ${x2},${y2}`, draft: e.draft }]
  })
}

let observer: ResizeObserver | null = null
onMounted(() => {
  measure()
  if (typeof ResizeObserver !== 'undefined' && area.value) {
    observer = new ResizeObserver(() => measure())
    observer.observe(area.value)
  }
})
onBeforeUnmount(() => observer?.disconnect())
watch(drawing, () => void nextTick(measure))
</script>

<template>
  <section class="material rounded-card p-5" :aria-labelledby="`${id}-h`" data-test="map-drawing">
    <h2 :id="`${id}-h`" class="mb-4 text-heading">{{ MAP.drawing.title }} <span lang="sv">{{ departmentName }}</span></h2>
    <div ref="area" class="relative">
      <svg class="pointer-events-none absolute inset-0 size-full overflow-visible max-md:hidden" aria-hidden="true" data-test="map-lines">
        <defs>
          <marker :id="`${id}-arrow`" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="8" markerHeight="8" orient="auto" class="fill-border-strong">
            <path d="M0,0 L8,4 L0,8 z" />
          </marker>
          <marker :id="`${id}-arrow-on`" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="8" markerHeight="8" orient="auto" class="fill-accent">
            <path d="M0,0 L8,4 L0,8 z" />
          </marker>
        </defs>
        <path
          v-for="l in shown" :key="l.key" :d="l.d" fill="none" stroke-width="1.5" :stroke-dasharray="l.draft ? '5 4' : undefined"
          :class="l.on ? 'stroke-accent' : lit ? 'stroke-border' : 'stroke-border-strong'" :marker-end="`url(#${id}-arrow${l.on ? '-on' : ''})`"
          :data-test="l.draft ? 'map-line-draft' : 'map-line'"
        />
      </svg>
      <div class="relative grid gap-6 md:grid-cols-4 md:gap-x-12">
        <section v-for="stage in STAGES" :key="stage" :aria-labelledby="`${id}-${stage}`" :data-test="`map-column-${stage}`">
          <h3 :id="`${id}-${stage}`" class="mb-2 text-caption font-medium uppercase tracking-wide text-fg-3">{{ MAP.drawing.columns[stage] }}</h3>
          <ul v-if="drawing.columns[stage].length" class="grid gap-3">
            <!-- The highlight is for the eye: a pointer gets it on hover, the keyboard from the focused link (focusin); the
                 relations are text under each item. -->
            <!-- eslint-disable-next-line vuejs-accessibility/no-static-element-interactions -->
            <li
              v-for="n in drawing.columns[stage]" :key="n.key" :data-node="n.key"
              class="rounded-control bg-surface px-3 py-2 shadow-inset-input transition-shadow"
              :class="{ '!bg-accent-soft !shadow-inset-selected': lit?.has(n.key) }" data-test="map-node"
              @mouseenter="active = n.key" @mouseleave="active = null" @focusin="active = n.key" @focusout="active = null"
            >
              <RouterLink v-if="n.to" :to="n.to" class="font-medium text-accent-ink underline">{{ n.name }}</RouterLink>
              <span v-else class="font-medium">{{ n.name }}</span>
              <span v-if="n.detail" class="block text-small text-fg-2">{{ n.detail }}</span>
              <span v-if="relation(n)" class="block text-small text-fg-2" data-test="map-relation">{{ relation(n) }}</span>
            </li>
          </ul>
          <p v-else class="m-0 text-fg-3">{{ MAP.drawing.noneInColumn }}</p>
        </section>
      </div>
    </div>
  </section>
</template>

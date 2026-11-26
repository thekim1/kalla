<script setup lang="ts">
/**
 * One of a page's summary tiles (layout rules in docs/admin-ui.md, rule 9): a title, the figure, a line under it and an optional
 * link or action in the `default` slot. Only a tile that needs action has a coloured border, and it says so in text
 * (`attention`), never by colour alone.
 */
import { useId } from 'vue'

defineProps<{ title: string; value: string; detail?: string; attention?: string | null; quiet?: boolean }>()
const titleId = `tile-${useId()}`
</script>

<template>
  <!-- A group named by its title, not a landmark or a heading (four tiles would add four regions and skip a level). -->
  <div
    class="material grid content-start gap-1 rounded-card p-4"
    :class="attention ? 'shadow-[inset_0_0_0_2px_var(--warn)]' : ''"
    role="group" :aria-labelledby="titleId"
    data-test="summary-tile"
  >
    <p :id="titleId" class="text-small font-medium text-fg-2">{{ title }}</p>
    <p class="text-heading tabular" :class="quiet ? 'text-fg-3' : ''" data-test="summary-tile-value">{{ value }}</p>
    <p v-if="detail" class="text-small text-fg-2" data-test="summary-tile-detail">{{ detail }}</p>
    <p v-if="attention" class="text-small font-medium text-warn" data-test="summary-tile-attention">{{ attention }}</p>
    <div v-if="$slots.default" class="mt-1 text-small"><slot /></div>
  </div>
</template>

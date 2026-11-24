<script setup lang="ts">
/**
 * One of a page's summary tiles (admin design §5.5.2 *Overview*): a title, the figure, a line under it and an optional
 * link or action in the `default` slot. Only a tile that needs action has a coloured border, and it says so in text
 * (`attention`), never by colour alone. Built in Källa until Fors takes it (§5.12, Shared components).
 */
defineProps<{ title: string; value: string; detail?: string; attention?: string | null; quiet?: boolean }>()
</script>

<template>
  <section
    class="material grid content-start gap-1 rounded-card p-4"
    :class="attention ? 'shadow-[inset_0_0_0_2px_var(--warn)]' : ''"
    :aria-label="title"
    data-test="summary-tile"
  >
    <h3 class="text-small font-medium text-fg-2">{{ title }}</h3>
    <p class="text-heading tabular" :class="quiet ? 'text-fg-3' : ''" data-test="summary-tile-value">{{ value }}</p>
    <p v-if="detail" class="text-small text-fg-2" data-test="summary-tile-detail">{{ detail }}</p>
    <p v-if="attention" class="text-small font-medium text-warn" data-test="summary-tile-attention">{{ attention }}</p>
    <div v-if="$slots.default" class="mt-1 text-small"><slot /></div>
  </section>
</template>

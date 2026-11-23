<script setup lang="ts">
import { computed } from 'vue'
import type { AttentionItem } from '@/api/types'
import AppIcon from '@/components/ui/AppIcon.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { ATTENTION as L } from '@/utils/labels'
import { attentionLink } from './attention'

// Home's Needs attention (admin design §5.5.1, B-59): one line per item of `GET /api/overview`'s attention list, its text
// by kind with the figures it names, and a link to the page where it is dealt with. Nothing to show: the card is absent.
const props = defineProps<{ items: AttentionItem[] }>()

const rows = computed(() =>
  props.items.map((item, i) => ({
    key: `${item.kind}:${item.objectType}:${item.objectId ?? item.objectName}:${i}`,
    text: (L.kinds[item.kind] ?? ((_: Record<string, string>, name: string) => L.unknown(name)))(item.details, item.objectName),
    link: attentionLink(item),
    action: item.kind === 'deletion_waiting' ? L.review : L.open,
  })),
)
</script>

<template>
  <UiCard v-if="items.length" :title="L.title(items.length)" data-test="needs-attention">
    <ul class="grid gap-2">
      <li v-for="(row, i) in rows" :key="row.key" class="flex flex-wrap items-center justify-between gap-2" data-test="attention-item">
        <span class="flex min-w-0 items-start gap-2">
          <AppIcon name="warning" class="mt-0.5 shrink-0 text-warn" aria-hidden="true" />
          <span :id="`attention-${i}`" class="min-w-0 break-words">{{ row.text }}</span>
        </span>
        <RouterLink v-if="row.link" :to="row.link" :aria-describedby="`attention-${i}`" class="shrink-0 text-small font-medium" data-test="attention-link">{{ row.action }}</RouterLink>
      </li>
    </ul>
  </UiCard>
</template>

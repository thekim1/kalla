<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api } from '@/api'
import type { QuarantinedFile } from '@/api/types'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { formatDate, formatDateTime } from '@/utils/format'
import { FILES } from '@/utils/labels'

// Karantän (admin design §4.5, connectors §11): the source's files ClamAV found malware in. Metadata only: name or URL,
// the signature, when it was found and when the blob is purged; never a download.
const props = defineProps<{ sourceId: string }>()
const items = ref<QuarantinedFile[]>([])
const next = ref<string | null>(null)
const loaded = ref(false)

async function load(append = false): Promise<void> {
  const page = await api.sources.quarantine(props.sourceId, append ? next.value ?? undefined : undefined).catch(() => null)
  loaded.value = true
  if (!page) return
  items.value = append ? [...items.value, ...page.items] : page.items
  next.value = page.next
}

onMounted(() => load())
</script>

<template>
  <UiCard :title="FILES.quarantine" data-test="quarantine">
    <p class="mb-3 text-small text-fg-2">{{ FILES.quarantineLead }}</p>
    <p v-if="loaded && items.length === 0" class="text-fg-2">{{ FILES.quarantineEmpty }}</p>
    <ul v-else class="grid gap-2">
      <li v-for="q in items" :key="q.id" class="border-b border-border pb-2">
        <span class="block break-all font-medium">{{ q.name ?? q.url }}</span>
        <span class="block text-small text-fg-2">
          {{ FILES.signature }}: <span class="font-mono">{{ q.signature }}</span> · {{ FILES.detected }} {{ formatDateTime(q.detectedAt) }} · {{ FILES.purge }} {{ formatDate(q.purgeAfter) }}
        </span>
      </li>
    </ul>
    <UiButton v-if="next" class="mt-3" size="sm" @click="load(true)">{{ FILES.more }}</UiButton>
  </UiCard>
</template>

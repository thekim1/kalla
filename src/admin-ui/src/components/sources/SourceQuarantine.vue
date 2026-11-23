<script setup lang="ts">
import { onMounted } from 'vue'
import { api } from '@/api'
import InlineError from '@/components/ui/InlineError.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useKeysetList } from '@/composables/useKeysetList'
import { formatDate, formatDateTime } from '@/utils/format'
import { FILES } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'

// Karantän (admin design §4.5, connectors §11): the source's files ClamAV found malware in. Metadata only: name or URL,
// the signature, when it was found and when the blob is purged; never a download.
const props = defineProps<{ sourceId: string }>()
const quarantine = useKeysetList((after?: string) => api.sources.quarantine(props.sourceId, after))

onMounted(() => quarantine.reload())
</script>

<template>
  <UiCard :title="FILES.quarantine" data-test="quarantine">
    <p class="mb-3 text-small text-fg-2">{{ FILES.quarantineLead }}</p>
    <InlineError v-if="quarantine.error.value" :message="problemMessage(quarantine.error.value)" class="mb-3" />
    <p v-if="quarantine.loaded.value && quarantine.items.value.length === 0" class="text-fg-2">{{ FILES.quarantineEmpty }}</p>
    <ul v-else class="grid gap-2">
      <li v-for="q in quarantine.items.value" :key="q.id" class="border-b border-border pb-2">
        <span class="block break-all font-medium">{{ q.name ?? q.url }}</span>
        <span class="block text-small text-fg-2">
          {{ FILES.signature }}: <span class="font-mono">{{ q.signature }}</span> · {{ FILES.detected }} {{ formatDateTime(q.detectedAt) }} · {{ FILES.purge }} {{ formatDate(q.purgeAfter) }}
        </span>
      </li>
    </ul>
    <UiButton v-if="quarantine.next.value" class="mt-3" size="sm" @click="quarantine.more()">{{ FILES.more }}</UiButton>
  </UiCard>
</template>

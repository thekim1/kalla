<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useKeysetList } from '@/composables/useKeysetList'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { formatDateTime, formatNumber } from '@/utils/format'
import { DELETIONS } from '@/utils/labels'
import { jobMessage } from '@/utils/messages'
import { problemMessage } from '@/utils/problem'

// Borttagning väntar på beslut (admin design §5.5.3, connectors §13.3–§13.5): the documents the run did not find and
// the guard held back, with their reasons and the CSV of all; Behåll dokumenten (reject: held) or Ta bort (confirm), each
// sending the count the user saw, so a newer run's proposal answers 409 deletion_superseded instead.
const props = defineProps<{ runId: string; proposedCount: number }>()
const emit = defineEmits<{ (e: 'decided'): void }>()
const auth = useAuthStore()
const ui = useUiStore()
const proposals = useKeysetList((after?: string) => api.runs.deletions(props.runId, after))
const guard = computed(() => proposals.data.value?.guard ?? null)
const deciding = ref<'' | 'keep' | 'remove'>('')
const canDecide = computed(() => auth.can('manage'))
const count = computed(() => formatNumber(props.proposedCount))

async function keep(): Promise<void> {
  await api.runs.rejectDeletions(props.runId, props.proposedCount)
  ui.notify(DELETIONS.kept)
  emit('decided')
}

async function remove(): Promise<void> {
  await api.runs.confirmDeletions(props.runId, props.proposedCount)
  ui.notify(DELETIONS.removed)
  emit('decided')
}

onMounted(() => proposals.reload())
</script>

<template>
  <UiCard :title="DELETIONS.title" data-test="deletions">
    <p>{{ DELETIONS.summary(count) }}</p>
    <p v-if="guard" class="mt-1 text-small text-fg-2" data-test="guard">{{ DELETIONS.why(guard) }}</p>
    <InlineError v-if="proposals.error.value" :message="problemMessage(proposals.error.value)" class="mt-3" />
    <h3 class="mb-1 mt-4 font-medium">{{ DELETIONS.examples }}</h3>
    <ul class="grid gap-2">
      <li v-for="p in proposals.items.value" :key="p.documentId" class="border-b border-border pb-2 text-small">
        <span class="block break-all font-medium" lang="sv">{{ p.title ?? p.url }}</span>
        <span v-if="p.title && p.url" class="block break-all text-fg-3">{{ p.url }}</span>
        <span class="block text-fg-2">
          {{ DELETIONS.reason }}: {{ jobMessage(p.reason) }} · {{ DELETIONS.lastSeen }} {{ formatDateTime(p.lastSeenAt) }}<template v-if="p.exempt"> · {{ DELETIONS.exempt }}</template>
        </span>
      </li>
    </ul>
    <div class="mt-3 flex flex-wrap gap-2">
      <UiButton v-if="proposals.next.value" size="sm" @click="proposals.more()">{{ DELETIONS.more }}</UiButton>
      <a :href="api.runs.deletionsCsvUrl(runId)" class="text-small text-accent-ink underline" download>{{ DELETIONS.csv }}</a>
    </div>
    <div v-if="canDecide" class="mt-4 flex flex-wrap gap-2">
      <UiButton variant="primary" data-test="keep" @click="deciding = 'keep'">{{ DELETIONS.keep }}</UiButton>
      <UiButton variant="danger" data-test="remove" @click="deciding = 'remove'">{{ DELETIONS.remove(count) }}</UiButton>
    </div>

    <ConfirmDialog
      :open="deciding === 'keep'" :title="DELETIONS.keepTitle" :consequence="DELETIONS.keepConsequence" :confirm-label="DELETIONS.keep" :action="keep"
      @update:open="!$event && (deciding = '')"
    />
    <ConfirmDialog
      :open="deciding === 'remove'" :title="DELETIONS.removeTitle(count)" :consequence="DELETIONS.removeConsequence" :confirm-label="DELETIONS.remove(count)"
      danger :action="remove" @update:open="!$event && (deciding = '')"
    />
  </UiCard>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api'
import type { HeldDocument } from '@/api/types'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { formatDateTime } from '@/utils/format'
import { DELETIONS } from '@/utils/labels'

// Behållna (admin design §5.5.3, connectors §13.5): documents kept by a rejected deletion; Släpp alla (command
// release-hold) makes them candidates again at the next reconcile.
const props = defineProps<{ sourceId: string }>()
const auth = useAuthStore()
const ui = useUiStore()
const items = ref<HeldDocument[]>([])
const next = ref<string | null>(null)
const loaded = ref(false)
const releasing = ref(false)
const canRelease = computed(() => auth.hasAnyRole(['rag-platform-admin', 'rag-department-admin']))

async function load(append = false): Promise<void> {
  const page = await api.sources.held(props.sourceId, append ? next.value ?? undefined : undefined).catch(() => null)
  loaded.value = true
  if (!page) return
  items.value = append ? [...items.value, ...page.items] : page.items
  next.value = page.next
}

async function release(): Promise<void> {
  await api.sources.releaseHeld(props.sourceId)
  ui.notify(DELETIONS.released)
}

onMounted(() => load())
</script>

<template>
  <UiCard :title="DELETIONS.held" data-test="held">
    <p class="mb-3 text-small text-fg-2">{{ DELETIONS.heldLead }}</p>
    <p v-if="loaded && items.length === 0" class="text-fg-2">{{ DELETIONS.heldEmpty }}</p>
    <template v-else>
      <ul class="grid gap-2">
        <li v-for="d in items" :key="d.id" class="border-b border-border pb-2 text-small">
          <span class="block break-all font-medium" lang="sv">{{ d.title ?? d.displayName ?? d.url }}</span>
          <span class="block text-fg-2">{{ DELETIONS.heldAt(formatDateTime(d.heldAt), d.heldBy) }} · {{ DELETIONS.lastSeen }} {{ formatDateTime(d.lastSeenAt) }}</span>
        </li>
      </ul>
      <div class="mt-3 flex flex-wrap gap-2">
        <UiButton v-if="next" size="sm" @click="load(true)">{{ DELETIONS.more }}</UiButton>
        <UiButton v-if="canRelease" size="sm" data-test="release" @click="releasing = true">{{ DELETIONS.release }}</UiButton>
      </div>
    </template>
    <ConfirmDialog
      v-model:open="releasing" :title="DELETIONS.releaseTitle" :consequence="DELETIONS.releaseConsequence" :confirm-label="DELETIONS.release" :action="release"
    />
  </UiCard>
</template>

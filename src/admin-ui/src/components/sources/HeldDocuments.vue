<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useKeysetList } from '@/composables/useKeysetList'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { formatDateTime } from '@/utils/format'
import { DELETIONS } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'

// Behållna (admin design §5.5.3, connectors §13.5): documents kept by a rejected deletion; Släpp alla (command
// release-hold) makes them candidates again at the next reconcile.
const props = defineProps<{ sourceId: string }>()
const auth = useAuthStore()
const ui = useUiStore()
const held = useKeysetList((after?: string) => api.sources.held(props.sourceId, after))
const releasing = ref(false)
const canRelease = computed(() => auth.can('manage'))

async function release(): Promise<void> {
  await api.sources.releaseHeld(props.sourceId)
  ui.notify(DELETIONS.released)
}

onMounted(() => held.reload())
</script>

<template>
  <!-- One part of the source's Held and quarantined card (admin design §5.5.3, §5.7). -->
  <section aria-labelledby="held-title" data-test="held">
    <h3 id="held-title" class="mb-2 font-medium">{{ DELETIONS.held }}</h3>
    <p class="mb-3 text-small text-fg-2">{{ DELETIONS.heldLead }}</p>
    <InlineError v-if="held.error.value" :message="problemMessage(held.error.value)" class="mb-3" />
    <p v-if="held.loaded.value && held.items.value.length === 0" class="text-fg-2">{{ DELETIONS.heldEmpty }}</p>
    <template v-else>
      <ul class="grid gap-2">
        <li v-for="d in held.items.value" :key="d.id" class="border-b border-border pb-2 text-small">
          <span class="block break-all font-medium" lang="sv">{{ d.title ?? d.displayName ?? d.url }}</span>
          <span class="block text-fg-2">{{ DELETIONS.heldAt(formatDateTime(d.heldAt), d.heldBy) }} · {{ DELETIONS.lastSeen }} {{ formatDateTime(d.lastSeenAt) }}</span>
        </li>
      </ul>
      <div class="mt-3 flex flex-wrap gap-2">
        <UiButton v-if="held.next.value" size="sm" @click="held.more()">{{ DELETIONS.more }}</UiButton>
        <UiButton v-if="canRelease" size="sm" data-test="release" @click="releasing = true">{{ DELETIONS.release }}</UiButton>
      </div>
    </template>
    <ConfirmDialog
      v-model:open="releasing" :title="DELETIONS.releaseTitle" :consequence="DELETIONS.releaseConsequence" :confirm-label="DELETIONS.release" :action="release"
    />
  </section>
</template>

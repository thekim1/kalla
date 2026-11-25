<script setup lang="ts">
import { computed, ref } from 'vue'
import { api } from '@/api'
import type { AttentionItem } from '@/api/types'
import AppIcon from '@/components/ui/AppIcon.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { formatDate } from '@/utils/format'
import { ASSISTANT, ATTENTION as L } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import { attentionLink } from './attention'

// Home's Needs attention (admin design §5.5.1, B-59): one line per item of `GET /api/overview`'s attention list, its text
// by kind with the figures it names, and a link to the page where it is dealt with. Nothing to show: the card is absent.
// An unused assistant (B-68) is dealt with here: **Archive** (confirmed, as on the assistant's page) or **Keep** (not
// raised again for 90 days), for those who may edit assistants; the list is then read again (`changed`).
const props = defineProps<{ items: AttentionItem[] }>()
const emit = defineEmits<{ (e: 'changed'): void }>()
const auth = useAuthStore()
const ui = useUiStore()
const canEdit = computed(() => auth.can('edit'))

/** The details as the text reads them: a day as a date. */
function readable(item: AttentionItem): Record<string, string> {
  return item.kind === 'assistant_unused' && item.details.lastUsed ? { ...item.details, lastUsed: formatDate(item.details.lastUsed) } : item.details
}

const rows = computed(() =>
  props.items.map((item, i) => ({
    item,
    key: `${item.kind}:${item.objectType}:${item.objectId ?? item.objectName}:${i}`,
    text: (L.kinds[item.kind] ?? ((_: Record<string, string>, name: string) => L.unknown(name)))(readable(item), item.objectName),
    link: attentionLink(item),
    action: item.kind === 'deletion_waiting' ? L.review : L.open,
    unused: item.kind === 'assistant_unused' && !!item.objectId && canEdit.value,
  })),
)

const busy = ref<string | null>(null)
const error = ref<string | null>(null)
const archiving = ref<AttentionItem | null>(null)

/** Archive or Keep with the assistant's current version (the item carries none). */
async function act(item: AttentionItem, what: 'archive' | 'keep'): Promise<void> {
  busy.value = item.objectId
  error.value = null
  try {
    const assistant = await api.assistants.get(item.objectId!)
    await api.assistants[what](item.objectId!, assistant.version)
    ui.notify(what === 'keep' ? L.kept(item.objectName) : L.archived(item.objectName))
    emit('changed')
  } catch (e) {
    error.value = problemMessage(e)
  } finally {
    busy.value = null
  }
}
</script>

<template>
  <UiCard v-if="items.length" :title="L.title(items.length)" data-test="needs-attention">
    <InlineError v-if="error" :message="error" class="mb-3" data-test="attention-error" />
    <ul class="grid gap-2">
      <li v-for="(row, i) in rows" :key="row.key" class="flex flex-wrap items-center justify-between gap-2" data-test="attention-item">
        <span class="flex min-w-0 items-start gap-2">
          <AppIcon name="warning" class="mt-0.5 shrink-0 text-warn" aria-hidden="true" />
          <span :id="`attention-${i}`" class="min-w-0 break-words">{{ row.text }}</span>
        </span>
        <span class="flex shrink-0 flex-wrap items-center gap-2">
          <template v-if="row.unused">
            <UiButton
              size="sm" variant="quiet" class="max-md:h-11" :aria-label="L.keepFor(row.item.objectName)" :disabled="busy !== null"
              :loading="busy === row.item.objectId" data-test="attention-keep" @click="act(row.item, 'keep')"
            >{{ L.keep }}</UiButton>
            <UiButton
              size="sm" variant="quiet" class="max-md:h-11" :aria-label="L.archiveFor(row.item.objectName)" :disabled="busy !== null"
              data-test="attention-archive" @click="archiving = row.item"
            >{{ L.archive }}</UiButton>
          </template>
          <RouterLink v-if="row.link" :to="row.link" :aria-describedby="`attention-${i}`" class="inline-flex min-h-6 items-center text-small font-medium max-md:min-h-11" data-test="attention-link">{{ row.action }}</RouterLink>
        </span>
      </li>
    </ul>
    <ConfirmDialog
      :open="archiving !== null" :title="ASSISTANT.archiveTitle" :consequence="ASSISTANT.archiveConsequence" :confirm-label="ASSISTANT.archive" danger
      :action="() => act(archiving!, 'archive')" @update:open="!$event && (archiving = null)"
    />
  </UiCard>
</template>

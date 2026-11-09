<script setup lang="ts">
import { computed } from 'vue'
import type { UploadFile } from '@/api/types'
import AppIcon from '@/components/ui/AppIcon.vue'
import CheckField from '@/components/ui/CheckField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { formatDateTime } from '@/utils/format'
import { FILES } from '@/utils/labels'
import { jobMessage } from '@/utils/messages'
import { baseName, groupByFolder, restorable, sizeText, stateTone } from './files'

// The rows of the Files list (admin design §5.5.7): grouped under folder headers when sorted by name (files without a
// folder first, under no header), else one list with each file's whole path. A row's name opens its details.
const props = defineProps<{
  files: UploadFile[]
  grouped: boolean
  selected: string[]
  canEdit: boolean
  showDeleted: boolean
  canRestore: boolean
}>()
const emit = defineEmits<{
  (e: 'update:selected', value: string[]): void
  (e: 'open', file: UploadFile): void
  (e: 'replace', file: UploadFile): void
  (e: 'restore', file: UploadFile): void
}>()

const groups = computed(() => (props.grouped ? groupByFolder(props.files) : [{ folder: '', files: props.files }]))
const selectable = computed(() => props.canEdit && !props.showDeleted)

function toggle(id: string, on: boolean): void {
  emit('update:selected', on ? [...props.selected, id] : props.selected.filter((x) => x !== id))
}
</script>

<template>
  <div class="grid gap-4" data-test="file-list">
    <section v-for="group in groups" :key="group.folder" :aria-label="group.folder || undefined" :data-test="group.folder ? `folder-${group.folder}` : 'folder-root'">
      <h4 v-if="group.folder" class="mb-2 flex items-center gap-2 break-all text-small font-medium text-fg-2" data-test="folder-header">
        <AppIcon name="folder" :size="18" />{{ group.folder }}
      </h4>
      <ul class="grid gap-2">
        <li v-for="f in group.files" :key="f.id" class="flex flex-wrap items-center gap-3 border-b border-border pb-2" :data-test="`file-${f.id}`">
          <CheckField
            v-if="selectable" :model-value="selected.includes(f.id)" :label="FILES.select(f.fileName)" hide-label
            @update:model-value="toggle(f.id, $event)"
          />
          <span class="min-w-0 flex-1">
            <button
              type="button" class="block break-all text-left font-medium hover:underline" :aria-label="FILES.openDetails(f.fileName)" :data-test="`open-${f.id}`"
              @click="emit('open', f)"
            >
              {{ grouped ? baseName(f.fileName) : f.fileName }}
            </button>
            <span class="block text-small text-fg-3">
              {{ sizeText(f.bytes) }} · {{ showDeleted ? FILES.deletedAt : FILES.uploaded }} {{ formatDateTime(f.deletedAt ?? f.uploadedAt) }}
            </span>
            <span v-if="f.reason" class="block text-small text-danger">{{ jobMessage(f.reason) }}</span>
          </span>
          <UiBadge :tone="stateTone(f.state)" :label="FILES.states[f.state] ?? f.state" />
          <UiButton v-if="selectable" size="sm" :aria-label="FILES.replaceLabel(f.fileName)" :data-test="`replace-${f.id}`" @click="emit('replace', f)">
            {{ FILES.replace }}
          </UiButton>
          <template v-if="showDeleted && canRestore">
            <UiButton v-if="restorable(f)" size="sm" :aria-label="FILES.restoreLabel(f.fileName)" :data-test="`restore-${f.id}`" @click="emit('restore', f)">
              {{ FILES.restore }}
            </UiButton>
            <span v-else class="text-small text-fg-3" :data-test="`restore-expired-${f.id}`">{{ FILES.restoreExpired }}</span>
          </template>
        </li>
      </ul>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api'
import type { Collection, SharingLevel } from '@/api/types'
import CheckField from '@/components/ui/CheckField.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useUiStore } from '@/stores/ui'
import { SHARING } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'

// Sharing (admin design §5.5.2, §3.2): who may use the knowledge base in their assistants. Only our department, selected
// departments or the whole municipality; shared use is read-only. Narrowing is refused while another department's
// published assistant links it (V35, `collection_shared_in_use`), and the refusal names those assistants.
const props = defineProps<{ collection: Collection; canManage: boolean }>()
const emit = defineEmits<{ (e: 'saved', collection: Collection): void }>()
const ui = useUiStore()
const departments = ref<{ id: string; name: string }[]>([])
const level = ref<SharingLevel>(props.collection.sharedWith)
const selected = ref<string[]>([...props.collection.sharedWithDepartmentIds])
const saving = ref(false)
const error = ref<unknown>(null)
const selectedError = ref<string | undefined>(undefined)
const LEVELS: SharingLevel[] = ['department', 'selected', 'municipality']
const owner = computed(() => departments.value.find((d) => d.id === props.collection.departmentId)?.name ?? '')
const others = computed(() => departments.value.filter((d) => d.id !== props.collection.departmentId))
const users = computed(() => props.collection.usedBy)
const departmentCount = computed(() => new Set(users.value.map((u) => u.departmentId)).size)
const columns: Column[] = [
  { key: 'name', label: SHARING.assistant },
  { key: 'department', label: SHARING.department },
  { key: 'topics', label: SHARING.topics },
  { key: 'status', label: SHARING.status },
]
const levelLabel = (l: SharingLevel) => (l === 'department' ? SHARING.only(owner.value) : l === 'selected' ? SHARING.selected : SHARING.municipality)
/** The refusal's assistants (V35), as "Name (Department)". */
const affected = computed(() => {
  const value = (error.value as { problem?: { affected?: unknown } } | null)?.problem?.affected
  return Array.isArray(value) ? value.map(String) : []
})

function toggle(id: string, on: boolean): void {
  selected.value = on ? [...new Set([...selected.value, id])] : selected.value.filter((x) => x !== id)
}

async function save(): Promise<void> {
  selectedError.value = level.value === 'selected' && selected.value.length === 0 ? SHARING.departmentsRequired : undefined
  if (selectedError.value) return
  saving.value = true
  error.value = null
  try {
    const saved = await api.collections.sharing(props.collection.id, {
      sharedWith: level.value, departmentIds: level.value === 'selected' ? selected.value : [], version: props.collection.version,
    })
    emit('saved', saved)
    ui.notify(SHARING.saved)
  } catch (e) {
    error.value = e
  } finally {
    saving.value = false
  }
}

onMounted(async () => {
  departments.value = await api.organisation.departmentNames().catch(() => [])
})
</script>

<template>
  <div class="grid max-w-[680px] gap-5">
    <fieldset class="grid gap-2">
      <legend class="mb-1 font-medium">{{ SHARING.question }}</legend>
      <label v-for="l in LEVELS" :key="l" class="flex gap-3 rounded-card border border-border p-3 has-checked:bg-accent-soft has-checked:shadow-inset-selected" :class="canManage ? 'cursor-pointer' : 'cursor-not-allowed'">
        <input :id="`sharing-${l}`" v-model="level" type="radio" name="sharing" :value="l" class="mt-1" :disabled="!canManage" />
        <span class="font-medium">{{ levelLabel(l) }}</span>
      </label>
    </fieldset>
    <fieldset v-if="level === 'selected'" class="grid gap-1" data-test="sharing-departments" :aria-describedby="selectedError ? 'sharing-departments-error' : undefined">
      <legend class="mb-1 font-medium">{{ SHARING.departments }}</legend>
      <CheckField
        v-for="d in others" :key="d.id" :label="d.name" :model-value="selected.includes(d.id)" :disabled="!canManage"
        @update:model-value="toggle(d.id, $event)"
      />
      <p v-if="selectedError" id="sharing-departments-error" class="text-small text-danger">{{ selectedError }}</p>
    </fieldset>
    <p class="text-fg-2">{{ SHARING.readOnly }}</p>
    <p v-if="!canManage" class="text-fg-3">{{ SHARING.manageOnly }}</p>
    <div class="grid gap-2">
      <h3 class="font-medium">{{ users.length ? SHARING.usedBy(users.length, departmentCount) : SHARING.notUsed }}</h3>
      <DataTable v-if="users.length" :columns="columns" :rows="users" :row-key="(u) => u.id" :caption="SHARING.usedBy(users.length, departmentCount)" data-test="sharing-used-by">
        <template #cell-name="{ row }"><span class="font-medium">{{ row.name }}</span></template>
        <template #cell-department="{ row }">{{ row.departmentName }}</template>
        <template #cell-topics="{ row }">{{ row.topics.length ? row.topics.join(', ') : SHARING.allTopics }}</template>
        <template #cell-status="{ row }">
          <UiBadge v-if="row.state === 'archived'" tone="neutral" :label="SHARING.archived" />
          <UiBadge v-else :tone="row.published ? 'ok' : 'neutral'" :label="row.published ? SHARING.published : SHARING.draft" />
        </template>
      </DataTable>
    </div>
    <InlineError v-if="error" :message="problemMessage(error) + (affected.length ? ` (${affected.join(', ')})` : '')" data-test="sharing-error" />
    <div v-if="canManage"><UiButton variant="primary" :loading="saving" data-test="save-sharing" @click="save">{{ SHARING.save }}</UiButton></div>
  </div>
</template>

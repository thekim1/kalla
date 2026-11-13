<script setup lang="ts">
import { computed, ref } from 'vue'
import type { AssistantSummary, Department, Level } from '@/api/types'
import CheckField from '@/components/ui/CheckField.vue'
import TextField from '@/components/ui/TextField.vue'
import { LEVEL_LABEL, PLATFORM_CLIENTS } from '@/utils/labels'
import { assistantAllowed } from './clients'

// The assistants an app offers (admin design §5.9.1), in the Add app drawer's third step and in the app's drawer: a
// searchable checklist. An assistant above the app's information class cannot be ticked (V7) and says why; one that is
// ticked already stays enabled, so it can be unticked.
const props = defineProps<{ idPrefix: string; assistants: AssistantSummary[]; departments: Department[]; ceiling: Level; error?: string | null }>()
const picked = defineModel<string[]>({ required: true })

const L = PLATFORM_CLIENTS.assistants
const query = ref('')
const departmentName = computed(() => new Map(props.departments.map((d) => [d.id, d.name])))
const rows = computed(() => {
  const words = query.value.trim().toLowerCase().split(/\s+/).filter(Boolean)
  return props.assistants.filter((a) => words.every((w) => a.name.toLowerCase().includes(w)))
})

const allowed = (a: AssistantSummary) => assistantAllowed(a.classification, props.ceiling)

function describe(a: AssistantSummary): string {
  return [departmentName.value.get(a.departmentId), a.published ? L.published : L.draft, LEVEL_LABEL[a.classification], !allowed(a) && L.aboveCeiling]
    .filter(Boolean)
    .join(' · ')
}

function toggle(id: string, on: boolean): void {
  picked.value = on ? [...new Set([...picked.value, id])] : picked.value.filter((x) => x !== id)
}
</script>

<template>
  <fieldset :id="`${idPrefix}-assistantIds`" class="grid gap-2" :aria-describedby="`${idPrefix}-assistants-hint`" data-test="assistant-picker">
    <legend class="mb-1 font-medium">{{ L.title }}</legend>
    <p :id="`${idPrefix}-assistants-hint`" class="text-small text-fg-3">{{ L.hint }}</p>
    <p v-if="assistants.length === 0" class="text-fg-2">{{ L.none }}</p>
    <template v-else>
      <TextField v-if="assistants.length > 8" :id="`${idPrefix}-assistant-search`" v-model="query" :label="L.search" type="search" autocomplete="off" />
      <div class="grid max-h-72 gap-1 overflow-auto">
        <CheckField
          v-for="a in rows" :key="a.id" :label="a.name" :description="describe(a)" :model-value="picked.includes(a.id)"
          :disabled="!allowed(a) && !picked.includes(a.id)" :data-test="`assistant-${a.id}`" @update:model-value="toggle(a.id, $event)"
        />
      </div>
      <p v-if="rows.length === 0" class="text-small text-fg-3">{{ L.noMatch }}</p>
      <p v-if="picked.length === 0" class="text-small text-fg-3" data-test="nothing-picked">{{ L.nothingPicked }}</p>
    </template>
    <p v-if="error" class="text-small text-danger" data-test="assistants-error">{{ error }}</p>
  </fieldset>
</template>

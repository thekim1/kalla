<script setup lang="ts">
import { computed, ref } from 'vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { PLATFORM } from '@/utils/labels'
import HostPolitenessDrawer from './HostPolitenessDrawer.vue'
import { differences, type HostOverrides, type HostPoliteness } from './politeness'

// The crawl section's host politeness (admin design §5.8; connectors design §6.6): one row per host with its preset, the
// settings that differ from it and the reason robots.txt is ignored; a drawer per host adds, edits or removes it. A
// mistake here changes how hard we fetch from someone's website, so it is a table and not JSON.
const props = defineProps<{ modelValue: HostOverrides }>()
const emit = defineEmits<{ (e: 'update:modelValue', value: HostOverrides): void }>()

const L = PLATFORM.politeness
const columns: Column[] = [
  { key: 'host', label: L.columns.host },
  { key: 'preset', label: L.columns.preset },
  { key: 'differs', label: L.columns.differs },
  { key: 'robots', label: L.columns.robots },
]

interface HostRow {
  host: string
  profile: HostPoliteness
}

const rows = computed<HostRow[]>(() =>
  Object.keys(props.modelValue)
    .sort()
    .map((host) => ({ host, profile: props.modelValue[host]! })),
)
const hosts = computed(() => Object.keys(props.modelValue))

const drawerOpen = ref(false)
// The host being edited (null: a new one); the drawer is mounted per opening.
const editing = ref<HostRow | null>(null)
const drawerKey = ref(0)

function open(row: HostRow | null): void {
  editing.value = row ?? { host: '', profile: {} }
  drawerKey.value++
  drawerOpen.value = true
}

function apply({ host, profile }: { host: string; profile: HostPoliteness }): void {
  // The host of an existing row cannot change in the drawer: a new host is a new row.
  emit('update:modelValue', { ...props.modelValue, [host]: profile })
  drawerOpen.value = false
}

function remove(): void {
  const next = { ...props.modelValue }
  if (editing.value?.host) delete next[editing.value.host]
  emit('update:modelValue', next)
  drawerOpen.value = false
}
</script>

<template>
  <section class="grid gap-3" data-test="host-politeness">
    <div class="flex items-start justify-between gap-4">
      <div>
        <h3 class="font-medium">{{ L.title }}</h3>
        <p class="text-small text-fg-3">{{ L.lead }}</p>
      </div>
      <UiButton icon="add" data-test="add-host" @click="open(null)">{{ L.add }}</UiButton>
    </div>
    <DataTable :columns="columns" :rows="rows" :row-key="(r) => r.host" :caption="L.title" :empty-text="L.empty" clickable @row-click="open">
      <template #cell-host="{ row }"><span class="font-mono">{{ row.host }}</span></template>
      <template #cell-preset="{ row }">{{ L.presets[row.profile.preset ?? ''] ?? row.profile.preset }}</template>
      <template #cell-differs="{ row }">{{ differences(row.profile).join(' · ') || '–' }}</template>
      <template #cell-robots="{ row }">{{ row.profile.robots?.mode === 'ignore' ? row.profile.robots.reason : '–' }}</template>
    </DataTable>
    <p class="text-small text-fg-3">{{ L.unsaved }}</p>
    <HostPolitenessDrawer
      v-if="editing" :key="drawerKey" v-model:open="drawerOpen" :host="editing.host || null" :profile="editing.profile" :taken-hosts="hosts"
      @apply="apply" @remove="remove"
    />
  </section>
</template>

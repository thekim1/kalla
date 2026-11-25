<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import type { RouteLocationRaw } from 'vue-router'
import { api } from '@/api'
import type { AuditDetail, AuditSummary } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import DetailRow from '@/components/ui/DetailRow.vue'
import FilterChip from '@/components/ui/FilterChip.vue'
import FilterTextChip from '@/components/ui/FilterTextChip.vue'
import PeriodPicker from '@/components/ui/PeriodPicker.vue'
import InlineError from '@/components/ui/InlineError.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import SelectField, { type SelectOption } from '@/components/ui/SelectField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useDebouncedWatch } from '@/composables/useDebouncedWatch'
import { useKeysetList } from '@/composables/useKeysetList'
import { IS_DAY, ONE_OF, useQueryFilters } from '@/composables/useQueryFilters'
import { SETTINGS_SECTIONS, type SettingsSectionName } from '@/platform/settingsPage'
import { actorText, formatDateTime } from '@/utils/format'
import { AUDIT, AUDIT_EVENTS, AUDIT_OBJECT_TYPES, FILTERS, PAGE, PLATFORM } from '@/utils/labels'
import { PERIODS, periodRange } from '@/utils/period'
import { problemMessage } from '@/utils/problem'

// Audit log (admin design §9.1): the audit log newest first by keyset, filtered by period, actor, event, type of object,
// object and service; the detail shows the stored before/after snapshot as JSON text (the diff viewer is P2). Entries are
// readable: the event in plain words (the code as its title), the object by name, linked to its page while it exists.
// Manage: department admins see their departments' entries (the API scopes them). Each read emits 6007 server-side.
// The filters are one row of chips kept in the address (§5.12 *Filters*): the period, *Who* and *Type of object*; the
// event, the service and the object id are under *More filters*.
const entries = useKeysetList((after?: number) => api.audit.list({ ...query.value, after, limit: 50 }))
/** `event` is an `AUDIT_EVENTS` key (`entityType.action`); picking one sets the type of object to its type. */
const { filters, active, clear } = useQueryFilters({
  period: '30d', from: '', to: '', actor: '', entityType: '', event: '', service: '', entityId: '',
}, {
  period: ONE_OF(PERIODS), from: IS_DAY, to: IS_DAY, entityType: ONE_OF(Object.keys(AUDIT_OBJECT_TYPES)), event: ONE_OF(Object.keys(AUDIT_EVENTS)),
})
const filterRow = ref<HTMLElement | null>(null)
const range = computed(() => periodRange(filters.period, filters.from, filters.to))
/** More filters opens itself while one of its filters is set. */
const moreOpen = ref(!!(filters.event || filters.service || filters.entityId))
const selectedRow = ref<AuditSummary | null>(null)
const selected = ref<AuditDetail | null>(null)
const detailOpen = ref(false)
const detailError = ref<unknown>(null)

const columns: Column[] = [
  { key: 'time', label: AUDIT.time },
  { key: 'actor', label: AUDIT.actor },
  { key: 'action', label: AUDIT.action },
  { key: 'entity', label: AUDIT.entity },
  { key: 'reason', label: AUDIT.reason },
]

/** A date field as the start of that day (from) or of the next day (to, exclusive), in local time. */
function day(value: string, next = false): string | undefined {
  if (!value) return undefined
  const date = new Date(`${value}T00:00:00`)
  if (Number.isNaN(date.getTime())) return undefined
  if (next) date.setDate(date.getDate() + 1)
  return date.toISOString()
}

/** An event's code as the code writes it: `entityType.action` (the type has no dot; an action may). */
function code(entry: { entityType: string; action: string }): string {
  return `${entry.entityType}.${entry.action}`
}
function typeOf(event: string): string {
  return event.slice(0, event.indexOf('.'))
}
function eventLabel(entry: { entityType: string; action: string }): string {
  return AUDIT_EVENTS[code(entry)] ?? code(entry)
}

function byLabel(a: SelectOption, b: SelectOption): number {
  return a.label.localeCompare(b.label, 'en')
}
const typeOptions = computed<SelectOption[]>(() => Object.entries(AUDIT_OBJECT_TYPES).map(([value, label]) => ({ value, label })).sort(byLabel))
/** The events of the chosen type of object, or every event. */
const eventOptions = computed<SelectOption[]>(() => [
  { value: '', label: AUDIT.any },
  ...Object.entries(AUDIT_EVENTS).filter(([event]) => !filters.entityType || typeOf(event) === filters.entityType)
    .map(([value, label]) => ({ value, label })).sort(byLabel),
])
// Immediate: an event read from the address brings its type of object, so the pair is consistent from the first load.
watch(() => filters.event, (event) => {
  if (event) filters.entityType = typeOf(event)
}, { immediate: true })
watch(() => filters.entityType, (type) => {
  if (filters.event && typeOf(filters.event) !== type) filters.event = ''
})

const query = computed(() => ({
  from: day(range.value.from), to: day(range.value.to, true), actor: filters.actor.trim() || undefined,
  action: filters.event ? filters.event.slice(typeOf(filters.event).length + 1) : undefined, entityType: filters.entityType || undefined,
  entityId: filters.entityId.trim() || undefined, service: filters.service.trim() || undefined,
}))

function settingsSection(entry: AuditSummary): SettingsSectionName | null {
  const id = entry.entityId
  return entry.entityType === 'settings' && (SETTINGS_SECTIONS as readonly string[]).includes(id ?? '') ? (id as SettingsSectionName) : null
}
/** The object's name: the API's, else a settings section's label; null when it has none (the page shows its id). */
function objectName(entry: AuditSummary): string | null {
  const section = settingsSection(entry)
  return entry.entityName ?? (section ? PLATFORM.settingsSections[section] : null)
}
/** The routes of the objects with a page, by type of object. */
const OBJECT_PAGES: Record<string, string> = { collection: 'collection', source: 'source', assistant: 'assistant', run: 'run' }
/** The object's page while it exists (a deleted object has none); a settings section's place on the Settings page. */
function objectLink(entry: AuditSummary): RouteLocationRaw | null {
  const section = settingsSection(entry)
  if (section) return { name: 'platform-settings', params: { section } }
  const page = OBJECT_PAGES[entry.entityType]
  return page && entry.entityId && entry.entityExists ? { name: page, params: { id: entry.entityId } } : null
}

/** Clear filters also folds More filters, which only stays open for a filter that is set. */
function clearAll(): void {
  clear(filterRow.value)
  moreOpen.value = false
}

/** Only this object: narrows the list to the row's object (type and id), shown under More filters. */
function only(entry: AuditSummary): void {
  if (!entry.entityId) return
  filters.entityType = entry.entityType
  filters.entityId = entry.entityId
  moreOpen.value = true
}

async function open(entry: AuditSummary): Promise<void> {
  detailOpen.value = true
  selectedRow.value = entry
  selected.value = null
  detailError.value = null
  try {
    selected.value = await api.audit.get(entry.id)
  } catch (e) {
    detailError.value = e
  }
}

/** The snapshot JSON indented for reading; the stored text when it doesn't parse. */
const details = computed(() => {
  const text = selected.value?.details
  if (!text) return null
  try {
    return JSON.stringify(JSON.parse(text), null, 2)
  } catch {
    return text
  }
})

useDebouncedWatch(() => ({ ...filters }), () => void entries.reload(), 300)
onMounted(() => entries.reload())
</script>

<template>
  <PageHeader :title="PAGE.audit.title" :description="AUDIT.lead" />
  <!-- One row of chips (admin design §5.12 Filters), kept in the address; Event, Service and the object under More filters. -->
  <form class="mb-6 grid gap-3" role="search" :aria-label="AUDIT.filters" @submit.prevent="entries.reload()">
    <div ref="filterRow" class="flex flex-wrap items-center gap-2" data-test="filters">
      <PeriodPicker v-model:period="filters.period" v-model:from="filters.from" v-model:to="filters.to" id-prefix="audit" />
      <FilterTextChip v-model="filters.actor" :label="AUDIT.actor" :any="AUDIT.actorHint" />
      <FilterChip v-model="filters.entityType" :label="AUDIT.entityType" :options="typeOptions" :any="AUDIT.any" />
      <UiButton v-if="active.length" size="sm" variant="quiet" class="max-md:h-11" data-test="clear" @click="clearAll">{{ FILTERS.clear }}</UiButton>
      <p class="ml-auto text-small text-fg-2" aria-live="polite" data-test="filter-count">
        {{ entries.loaded.value ? AUDIT.shown(entries.items.value.length, !!entries.next.value) : '' }}
      </p>
    </div>
    <details :open="moreOpen" data-test="more-filters" @toggle="moreOpen = ($event.target as HTMLDetailsElement).open">
      <summary class="cursor-pointer font-medium">{{ AUDIT.moreFilters }}</summary>
      <div class="mt-4 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        <SelectField id="audit-event" v-model="filters.event" :label="AUDIT.action" :options="eventOptions" />
        <TextField id="audit-service" v-model="filters.service" :label="AUDIT.service" :hint="AUDIT.serviceHint" mono />
        <TextField id="audit-entity-id" v-model="filters.entityId" :label="AUDIT.entityId" mono />
      </div>
    </details>
  </form>

  <AsyncState
    :loading="entries.loading.value && entries.items.value.length === 0" :error="entries.error.value"
    :empty="entries.items.value.length === 0" :empty-text="AUDIT.empty" @retry="entries.reload()"
  >
    <DataTable :columns="columns" :rows="entries.items.value" :row-key="(e) => String(e.id)" :caption="AUDIT.caption" clickable @row-click="open">
      <template #cell-time="{ row }"><span class="tabular whitespace-nowrap">{{ formatDateTime(row.timestamp, true) }}</span></template>
      <template #cell-actor="{ row }">
        <span class="font-medium">{{ actorText(row.actor, row.actorName) }}</span>
        <span v-if="row.service" class="block font-mono text-small text-fg-3">{{ row.service }}</span>
      </template>
      <template #cell-action="{ row }"><span :title="code(row)" data-test="event">{{ eventLabel(row) }}</span></template>
      <template #cell-entity="{ row }">
        <span class="flex flex-wrap items-baseline gap-x-3">
          <RouterLink
            v-if="objectLink(row)" :to="objectLink(row)!" class="inline-flex min-h-6 items-center text-accent-ink underline" :title="row.entityId ?? undefined" data-test="object-link" @click.stop
          >
            {{ objectName(row) ?? row.entityId }}
          </RouterLink>
          <span v-else-if="objectName(row)" :title="row.entityId ?? undefined" data-test="object-name">{{ objectName(row) }}</span>
          <span v-else class="break-all font-mono text-small text-fg-2" data-test="object-name">{{ row.entityId ?? '–' }}</span>
          <button
            v-if="row.entityId" type="button" class="inline-flex min-h-6 items-center text-small text-accent-ink underline" :aria-label="AUDIT.onlyThisLabel(objectName(row) ?? row.entityId)"
            data-test="only-this" @click.stop="only(row)"
          >
            {{ AUDIT.onlyThis }}
          </button>
        </span>
      </template>
      <template #cell-reason="{ row }"><span class="text-fg-2">{{ row.reason ?? '' }}</span></template>
    </DataTable>
    <UiButton v-if="entries.next.value" class="mt-4" :loading="entries.loading.value" data-test="more" @click="entries.more()">{{ AUDIT.more }}</UiButton>
  </AsyncState>

  <UiDrawer v-model:open="detailOpen" :title="AUDIT.detail" :subtitle="selected ? `#${selected.id}` : undefined" wide>
    <InlineError v-if="detailError" :message="problemMessage(detailError)" />
    <div v-else-if="selected" class="grid gap-1" data-test="detail">
      <dl class="m-0">
        <DetailRow :label="AUDIT.time">{{ formatDateTime(selected.timestamp, true) }}</DetailRow>
        <DetailRow :label="AUDIT.actor">{{ actorText(selected.actor, selected.actorName) === selected.actor ? selected.actor : `${actorText(selected.actor, selected.actorName)} (${selected.actor})` }}</DetailRow>
        <DetailRow v-if="selected.actorRoles" :label="AUDIT.roles"><span class="font-mono text-small">{{ selected.actorRoles }}</span></DetailRow>
        <DetailRow :label="AUDIT.action">
          {{ eventLabel(selected) }} <span class="font-mono text-small text-fg-3" data-test="detail-code">{{ code(selected) }}</span>
        </DetailRow>
        <DetailRow v-if="selectedRow && objectName(selectedRow)" :label="AUDIT.entity">{{ objectName(selectedRow) }}</DetailRow>
        <DetailRow v-if="selected.entityId" :label="AUDIT.entityId"><span class="break-all font-mono text-small">{{ selected.entityId }}</span></DetailRow>
        <DetailRow v-if="selected.service" :label="AUDIT.service"><span class="font-mono">{{ selected.service }}</span></DetailRow>
        <DetailRow v-if="selected.classification" :label="AUDIT.classification">{{ selected.classification }}</DetailRow>
        <DetailRow v-if="selected.reason" :label="AUDIT.reason">{{ selected.reason }}</DetailRow>
        <DetailRow v-if="selected.requestId" :label="AUDIT.requestId"><span class="break-all font-mono text-small">{{ selected.requestId }}</span></DetailRow>
      </dl>
      <h3 class="mt-4 font-medium">{{ AUDIT.snapshot }}</h3>
      <pre v-if="details" class="max-h-[60vh] overflow-auto rounded-tile bg-sunken p-3 font-mono text-small" lang="en" data-test="snapshot">{{ details }}</pre>
      <p v-else class="text-fg-2">{{ AUDIT.noSnapshot }}</p>
    </div>
  </UiDrawer>
</template>

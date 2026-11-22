<script setup lang="ts">
import { computed } from 'vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextField from '@/components/ui/TextField.vue'
import { SOURCE } from '@/utils/labels'
import { type SchedulePreset, describeSchedule } from './schedule'

// When the source is fetched again (engine design §9.6), in the URL-list and crawl forms: the platform default for the type,
// every night, every week or a cron of its own. `defaultSchedule` is the default's cron when known (the edit form), so the
// option can say what it is.
const props = defineProps<{ idPrefix: string; preset: SchedulePreset; custom: string; defaultSchedule?: string | null; error?: string | null }>()
const emit = defineEmits<{ (e: 'update:preset', value: SchedulePreset): void; (e: 'update:custom', value: string): void }>()

const options = computed(() =>
  (['default', 'nightly', 'weekly', 'custom'] as const).map((p) => ({
    value: p,
    label: p === 'default' && props.defaultSchedule ? SOURCE.scheduleDefaultIs(describeSchedule(props.defaultSchedule)!) : SOURCE.schedulePresets[p],
  })),
)
</script>

<template>
  <div class="grid gap-5" data-test="schedule-fields">
    <SelectField
      :id="`${idPrefix}-schedule`" :model-value="preset" :label="SOURCE.schedule" :hint="SOURCE.scheduleHint" :options="options"
      @update:model-value="emit('update:preset', $event as SchedulePreset)"
    />
    <TextField
      v-if="preset === 'custom'" :id="`${idPrefix}-schedule-custom`" :model-value="custom" :label="SOURCE.scheduleCustom"
      :hint="SOURCE.scheduleCustomHint" :error="error" mono autocomplete="off" @update:model-value="emit('update:custom', $event)"
    />
  </div>
</template>

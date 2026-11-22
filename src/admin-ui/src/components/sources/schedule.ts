import { SOURCE } from '@/utils/labels'

// A source's schedule (engine design §9.6; admin design §4.5 "schedule default per type"): empty runs on the platform's
// default for the type (Settings → Worker), a preset or any 5-field cron in Swedish time. The API checks the cron with
// Cronos (CronSchedules.IsValid); the form checks only the shape the API's pattern asks for.

export type SchedulePreset = 'default' | 'nightly' | 'weekly' | 'custom'

/** The presets' cron; nightly and weekly are also the design's defaults for URL lists and crawls. */
export const SCHEDULE_CRON: Record<'nightly' | 'weekly', string> = { nightly: '0 2 * * *', weekly: '0 1 * * 6' }

/** The form's state for a saved schedule. */
export function scheduleForm(schedule: string | null): { preset: SchedulePreset; custom: string } {
  if (!schedule) return { preset: 'default', custom: '' }
  const preset = (Object.keys(SCHEDULE_CRON) as ('nightly' | 'weekly')[]).find((p) => SCHEDULE_CRON[p] === schedule)
  return preset ? { preset, custom: '' } : { preset: 'custom', custom: schedule }
}

/** What the form saves: null for the default, the cron otherwise (fields separated by one space). */
export function scheduleValue(preset: SchedulePreset, custom: string): string | null {
  if (preset === 'default') return null
  if (preset === 'custom') return custom.trim().split(/\s+/).join(' ')
  return SCHEDULE_CRON[preset]
}

/** Five fields; Cronos decides the rest on save. */
export function scheduleShapeValid(preset: SchedulePreset, custom: string): boolean {
  return preset !== 'custom' || /^\S+(\s+\S+){4}$/.test(custom.trim())
}

/** A cron in words when it is a preset, else the cron itself. */
export function describeSchedule(cron: string | null): string | null {
  if (!cron) return null
  const preset = (Object.keys(SCHEDULE_CRON) as ('nightly' | 'weekly')[]).find((p) => SCHEDULE_CRON[p] === cron)
  return preset ? SOURCE.schedulePresets[preset] : cron
}

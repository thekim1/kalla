import { computed, ref } from 'vue'

/**
 * A form's unsaved changes (admin design §5.12 rule 7, *Saving*): `snapshot` returns the form's values by field, `reset()`
 * takes them as saved (after a load, a save or a discard), and `changes` counts the fields that differ from that, so the
 * save bar can say "3 unsaved changes". Values compare as JSON; a plain object's own fields count one by one (a group
 * of settings is several fields), an array as one. Nothing counts until the first `reset()`.
 */
export function useDirty(snapshot: () => Record<string, unknown>) {
  const saved = ref<Record<string, string> | null>(null)
  const isGroup = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)
  const encode = (values: Record<string, unknown>) => Object.fromEntries(
    Object.entries(values).flatMap(([k, v]) =>
      isGroup(v) ? Object.entries(v).map(([f, w]) => [`${k}.${f}`, JSON.stringify(w ?? null)]) : [[k, JSON.stringify(v ?? null)]]),
  )

  const changes = computed(() => {
    const base = saved.value
    if (!base) return 0
    const now = encode(snapshot())
    // Both sides' fields: a field a group no longer has is a change too.
    return [...new Set([...Object.keys(base), ...Object.keys(now)])].filter((k) => base[k] !== now[k]).length
  })

  return {
    changes,
    dirty: computed(() => changes.value > 0),
    reset: () => (saved.value = encode(snapshot())),
  }
}

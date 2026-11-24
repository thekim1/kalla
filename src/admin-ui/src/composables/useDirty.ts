import { computed, ref } from 'vue'

/**
 * A form's unsaved changes (admin design §5.12 rule 7, *Saving*): `snapshot` returns the form's values by field, `reset()`
 * takes them as saved (after a load, a save or a discard), and `changes` counts the fields that differ from that, so the
 * save bar can say "3 unsaved changes". Values compare as JSON. Nothing counts until the first `reset()`.
 */
export function useDirty(snapshot: () => Record<string, unknown>) {
  const saved = ref<Record<string, string> | null>(null)
  const encode = (values: Record<string, unknown>) => Object.fromEntries(Object.entries(values).map(([k, v]) => [k, JSON.stringify(v ?? null)]))

  const changes = computed(() => {
    const base = saved.value
    if (!base) return 0
    return Object.entries(encode(snapshot())).filter(([k, v]) => base[k] !== v).length
  })

  return {
    changes,
    dirty: computed(() => changes.value > 0),
    reset: () => (saved.value = encode(snapshot())),
  }
}

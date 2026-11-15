<script setup lang="ts">
import { computed, ref } from 'vue'
import type { SecretExpiry } from '@/api/types'
import AppIcon from '@/components/ui/AppIcon.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { formatDate, todayIso } from '@/utils/format'
import { OVERVIEW } from '@/utils/labels'
import { readStoredIds, writeStoredIds } from '@/utils/storedIds'

// Home's reminder before a client secret stored by the setup wizard expires (admin design §5.11), for platform admins: one notice
// per secret with its date, the days left (or since), and how to renew it. No link to /setup: it is closed until re-opened.
// Hide is remembered per browser for that secret and date, so a new date shows again.
const props = defineProps<{ entries: SecretExpiry[] }>()
const L = OVERVIEW.secretExpiry

const HIDDEN_STORAGE_KEY = 'ume-home-hidden-secret-expiry'
const hidden = ref<string[]>(readStoredIds(HIDDEN_STORAGE_KEY))
const idOf = (entry: SecretExpiry) => `${entry.which}:${entry.expiresOn}`

/** Whole days from today (Stockholm) to `yyyy-mm-dd`, negative once it has passed. */
function daysUntil(date: string, today: string): number {
  const utc = (iso: string) => {
    const [y, m, d] = iso.split('-').map(Number) as [number, number, number]
    return Date.UTC(y, m - 1, d)
  }
  return Math.round((utc(date) - utc(today)) / 86_400_000)
}

const notices = computed(() => {
  const today = todayIso()
  return props.entries.filter((e) => !hidden.value.includes(idOf(e))).map((e) => {
    const client = L.clients[e.which]
    const days = daysUntil(e.expiresOn, today)
    const date = formatDate(e.expiresOn)
    return { id: idOf(e), which: e.which, client, text: days < 0 ? L.expired(client, date, -days) : L.expires(client, date, days) }
  })
})

function hide(id: string): void {
  // Ids of reminders no longer listed (renewed, or another date) are dropped on the way.
  hidden.value = [...hidden.value.filter((h) => props.entries.some((e) => idOf(e) === h)), id]
  writeStoredIds(HIDDEN_STORAGE_KEY, hidden.value)
}
</script>

<template>
  <ul v-if="notices.length" class="grid gap-3" data-test="secret-expiry">
    <li v-for="n in notices" :key="n.id" class="flex items-start gap-2 rounded-card bg-warn-soft p-4 text-warn" :data-test="`secret-expiry-${n.which}`">
      <AppIcon name="warning" :size="18" filled class="mt-px" />
      <div class="grid min-w-0 flex-1 gap-1 text-small">
        <p class="font-medium" data-test="secret-expiry-text">{{ n.text }}</p>
        <p class="text-fg">{{ L.steps }}</p>
      </div>
      <UiButton variant="quiet" size="sm" :aria-label="L.hideFor(n.client)" data-test="secret-expiry-hide" @click="hide(n.id)">{{ L.hide }}</UiButton>
    </li>
  </ul>
</template>

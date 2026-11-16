<script setup lang="ts">
import { computed, ref } from 'vue'
import type { DeploymentNotice } from '@/api/types'
import AppIcon from '@/components/ui/AppIcon.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { OVERVIEW } from '@/utils/labels'
import { readStoredIds, writeStoredIds } from '@/utils/storedIds'

// Home's deployment notices (admin design §5.5.1), for platform admins: what the person who runs the deployment should
// tidy after an upgrade. Today one code, the retired variables the stack still sets (Decisions "Deployment values
// across releases"); a code without texts here is not shown. Hide is remembered per browser for that code and those
// names, so a notice about other names shows again.
const props = defineProps<{ notices: DeploymentNotice[] }>()
const L = OVERVIEW.deployment
const TEXTS = { retired_variables: L.retired_variables } as const
const known = (code: string): code is keyof typeof TEXTS => code in TEXTS

const HIDDEN_STORAGE_KEY = 'ume-home-hidden-deployment-notices'
const hidden = ref<string[]>(readStoredIds(HIDDEN_STORAGE_KEY))
const idOf = (notice: DeploymentNotice) => `${notice.code}:${[...notice.names].sort().join(',')}`

const shown = computed(() =>
  props.notices.flatMap((n) => {
    const id = idOf(n)
    if (!known(n.code) || n.names.length === 0 || hidden.value.includes(id)) return []
    const texts = TEXTS[n.code]
    return [{ id, code: n.code, names: n.names, title: texts.title(n.names.length), text: texts.text, hideFor: texts.hideFor }]
  }),
)

function hide(id: string): void {
  // Ids of notices no longer listed (tidied, or other names) are dropped on the way.
  hidden.value = [...hidden.value.filter((h) => props.notices.some((n) => idOf(n) === h)), id]
  writeStoredIds(HIDDEN_STORAGE_KEY, hidden.value)
}
</script>

<template>
  <ul v-if="shown.length" class="grid gap-3" data-test="deployment-notices">
    <li v-for="n in shown" :key="n.id" class="flex items-start gap-2 rounded-card bg-sunken p-4" :data-test="`deployment-notice-${n.code}`">
      <AppIcon name="info" :size="18" class="mt-px text-fg-2" />
      <div class="grid min-w-0 flex-1 gap-1 text-small">
        <p class="font-medium" data-test="deployment-notice-title">{{ n.title }}</p>
        <ul class="flex flex-wrap gap-x-3 gap-y-1" data-test="deployment-notice-names">
          <li v-for="name in n.names" :key="name" class="tabular break-all">{{ name }}</li>
        </ul>
        <p class="text-fg-2">{{ n.text }}</p>
      </div>
      <UiButton variant="quiet" size="sm" :aria-label="n.hideFor" data-test="deployment-notice-hide" @click="hide(n.id)">{{ L.hide }}</UiButton>
    </li>
  </ul>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import AppIcon from '@/components/ui/AppIcon.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { SIGN_IN } from '@/utils/labels'

// Adapted from the gateway: Swedish texts, and the refused sign-in of identity design §1.7 (`/?signin=refused&reason=…`).
// Setup mode (identity design §19): while setup is open the first action is the wizard; without a usable provider there is
// no sign-in to start.
defineProps<{ failed?: boolean }>()
const auth = useAuthStore()
const route = useRoute()
const router = useRouter()
const organisation = computed(() => auth.user?.organisationName ?? null)
const href = computed(() => auth.loginUrl(route.path))
const expired = computed(() => auth.user !== null && !auth.user.isAuthenticated && !!auth.user.name)
const refused = computed(() => {
  if (route.query.signin !== 'refused') return null
  const reason = typeof route.query.reason === 'string' ? route.query.reason : ''
  return SIGN_IN.refused[reason]?.(organisation.value) ?? SIGN_IN.refusedFallback
})
const setupOpen = computed(() => auth.user?.setup.open === true)
/** Without an answer from /bff/user (`failed`) the sign-in button stays, as before setup mode. */
const configured = computed(() => auth.user?.signIn.configured !== false)
const setupHref = router.resolve({ name: 'setup' }).href

function openSetup(event: MouseEvent): void {
  event.preventDefault()
  void router.push({ name: 'setup' })
}
</script>

<template>
  <main id="main" tabindex="-1" class="flex min-h-screen items-center justify-center p-4 outline-none">
    <div class="material-overlay flex w-full max-w-md flex-col gap-5 rounded-dialog p-8">
      <span class="flex h-11 w-11 items-center justify-center rounded-control bg-fg text-on-fg" aria-hidden="true"><AppIcon name="hub" /></span>
      <div>
        <h1 id="page-title" tabindex="-1" class="text-title-2 text-fg outline-none">{{ SIGN_IN.title }}</h1>
        <p class="mt-1 text-body text-fg-2">{{ SIGN_IN.lead(organisation) }}</p>
      </div>
      <p v-if="failed" role="alert" class="flex gap-2 text-small text-danger"><AppIcon name="error" /> {{ SIGN_IN.failed }}</p>
      <p v-else-if="refused" role="alert" class="flex gap-2 text-small text-danger"><AppIcon name="block" /> {{ refused }}</p>
      <p v-else-if="expired" role="status" class="flex gap-2 text-small text-fg-2"><AppIcon name="info" /> {{ SIGN_IN.expired }}</p>
      <template v-if="setupOpen">
        <p class="flex gap-2 text-small text-fg-2" data-test="setup-open"><AppIcon name="info" /> {{ SIGN_IN.setupOpen }}</p>
        <UiButton variant="primary" size="lg" icon="settings" :href="setupHref" data-test="set-up" @click="openSetup">{{ SIGN_IN.setUp }}</UiButton>
        <UiButton v-if="configured" size="lg" icon="login" :href="href" data-test="sign-in">{{ SIGN_IN.button }}</UiButton>
      </template>
      <p v-else-if="!configured" role="status" class="flex gap-2 text-small text-fg-2" data-test="not-configured"><AppIcon name="info" /> {{ SIGN_IN.notConfigured }}</p>
      <UiButton v-else variant="primary" size="lg" icon="login" :href="href" data-test="sign-in">{{ SIGN_IN.button }}</UiButton>
    </div>
  </main>
</template>

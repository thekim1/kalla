<script setup lang="ts">
import type { SetupState } from '@/api/types'
import DetailRow from '@/components/ui/DetailRow.vue'
import CopyValue from '@/platform/CopyValue.vue'
import { SETUP_WIZARD } from '@/utils/labels'

// Step 2 (admin design §5.11): the redirect URIs to register at the provider, and what kalla-admin knows the deployment set.
defineProps<{ state: SetupState }>()
const L = SETUP_WIZARD.addresses
</script>

<template>
  <div class="grid gap-6" data-test="addresses-step">
    <div class="grid gap-4">
      <p class="text-fg-2">{{ L.redirectLead }}</p>
      <div class="grid gap-1.5">
        <span class="font-medium">{{ L.signIn }}</span>
        <CopyValue :value="state.redirectUris.signIn" :copy-label="L.copySignIn" data-test="redirect-sign-in" />
      </div>
      <div class="grid gap-1.5">
        <span class="font-medium">{{ L.signOut }}</span>
        <CopyValue :value="state.redirectUris.signOutCallback" :copy-label="L.copySignOut" data-test="redirect-sign-out" />
      </div>
    </div>
    <section aria-labelledby="setup-deployment-facts">
      <h3 id="setup-deployment-facts" class="text-heading">{{ L.deploymentTitle }}</h3>
      <p class="mt-1 text-small text-fg-3">{{ L.deploymentLead }}</p>
      <dl class="mt-2" data-test="deployment-facts">
        <DetailRow :label="L.gateway"><span :class="{ 'text-fg-3': !state.deployment.gatewayBaseUrl }">{{ state.deployment.gatewayBaseUrl ?? L.notSet }}</span></DetailRow>
        <DetailRow :label="L.apiPublicUrl"><span :class="{ 'text-fg-3': !state.deployment.apiPublicUrl }">{{ state.deployment.apiPublicUrl ?? L.notSet }}</span></DetailRow>
        <DetailRow :label="L.platformAdminGroups">
          <span v-if="state.deployment.platformAdminGroups.length" class="font-mono text-small">{{ state.deployment.platformAdminGroups.join(', ') }}</span>
          <span v-else class="text-fg-3">{{ L.notSet }}</span>
        </DetailRow>
      </dl>
    </section>
  </div>
</template>

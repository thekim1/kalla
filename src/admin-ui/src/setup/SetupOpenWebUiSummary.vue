<script setup lang="ts">
import { computed } from 'vue'
import type { SetupOpenWebUiState, SignInValueSource } from '@/api/types'
import DetailRow from '@/components/ui/DetailRow.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import { SETUP_WIZARD } from '@/utils/labels'
import type { OpenWebUiOutcome } from './setupValues'

// Open WebUI's part of the summary (admin design §5.11 step 5): its values as they apply now, each with its source, where its
// secret comes from, and whether they were saved in this session. Finish does not depend on them.
const props = defineProps<{ openWebUi: SetupOpenWebUiState; outcome: OpenWebUiOutcome }>()
const L = SETUP_WIZARD.summary
const O = L.openWebUi
const W = SETUP_WIZARD.openWebUi

const rows = computed(() => {
  const s = props.openWebUi.signIn
  return [
    { key: 'providerUrl', label: W.providerUrl, ...s.providerUrl },
    { key: 'clientId', label: SETUP_WIZARD.signIn.clientId, ...s.clientId },
    { key: 'providerName', label: W.providerName, ...s.providerName },
  ]
})
const secretText = computed(() => {
  const secret = props.openWebUi.signIn.clientSecret
  if (secret.source === 'deployment') return L.secret.deployment
  if (secret.unreadable) return O.secretUnreadable
  return secret.set ? O.secretStored : O.secretNone
})
const tone = (source: SignInValueSource) => (source === 'deployment' ? 'accent' : source === 'wizard' ? 'ok' : 'neutral')
</script>

<template>
  <section aria-labelledby="setup-openwebui-summary" data-test="summary-openwebui">
    <h3 id="setup-openwebui-summary" class="text-heading">{{ O.title }}</h3>
    <p class="mt-1 text-small text-fg-3">{{ O.lead }}</p>
    <dl class="mt-2">
      <DetailRow :label="O.outcomeLabel"><span data-test="summary-openwebui-outcome">{{ O.outcome[outcome] }}</span></DetailRow>
      <DetailRow v-for="r in rows" :key="r.key" :label="r.label">
        <span class="flex flex-wrap items-center gap-2" :data-test="`summary-openwebui-${r.key}`">
          <span v-if="r.value" class="break-all font-mono text-small">{{ r.value }}</span>
          <UiBadge :tone="tone(r.source)" :label="L.sources[r.source]" :dot="false" />
        </span>
      </DetailRow>
      <DetailRow :label="SETUP_WIZARD.signIn.clientSecret"><span data-test="summary-openwebui-secret">{{ secretText }}</span></DetailRow>
      <DetailRow v-if="openWebUi.signIn.clientSecret.expiresOn" :label="SETUP_WIZARD.signIn.expiresOn">{{ openWebUi.signIn.clientSecret.expiresOn }}</DetailRow>
    </dl>
  </section>
</template>

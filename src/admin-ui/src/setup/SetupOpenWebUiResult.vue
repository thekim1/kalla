<script setup lang="ts">
import { computed } from 'vue'
import type { OpenWebUiTest } from '@/api/types'
import AppIcon from '@/components/ui/AppIcon.vue'
import DetailRow from '@/components/ui/DetailRow.vue'
import InlineError from '@/components/ui/InlineError.vue'
import { SETUP_WIZARD } from '@/utils/labels'
import { openWebUiCheckMessage } from '@/utils/messages'

// Open WebUI's check result (admin design §5.11 step 4, identity design §19): the discovery document (found, or the check was
// refused instead) and the client: accepted, refused (the check failed), or could not be verified (saving is still allowed).
const props = defineProps<{ test: OpenWebUiTest | null; stale: boolean }>()
const L = SETUP_WIZARD.openWebUiResult

const result = computed(() => props.test?.result ?? null)
const client = computed(() => {
  const accepted = result.value?.clientAccepted
  return accepted === true ? 'accepted' : accepted === false ? 'refused' : 'unverified'
})
</script>

<template>
  <div v-if="test" aria-live="polite" class="grid gap-3" data-test="openwebui-result">
    <InlineError v-if="stale" tone="warn" :message="L.stale" data-test="openwebui-stale" />
    <InlineError v-if="test.status === 'failed'" :title="L.failed" :message="openWebUiCheckMessage(test.reason ?? '')" data-test="openwebui-failed" />
    <dl v-if="result">
      <DetailRow :label="L.discovery">
        <span class="flex items-center gap-2 font-medium text-ok"><AppIcon name="check_circle" :size="18" filled />{{ L.discoveryOk }}</span>
      </DetailRow>
      <DetailRow :label="L.client">
        <span
          class="flex items-center gap-2 font-medium" :class="{ 'text-ok': client === 'accepted', 'text-danger': client === 'refused', 'text-warn': client === 'unverified' }"
          :data-test="`openwebui-client-${client}`"
        >
          <AppIcon :name="client === 'accepted' ? 'check_circle' : client === 'refused' ? 'error' : 'warning'" :size="18" filled />{{ L[client] }}
        </span>
      </DetailRow>
    </dl>
    <div v-if="result && client === 'unverified'" class="grid gap-1 text-small" data-test="openwebui-unverified-help">
      <p>{{ openWebUiCheckMessage(result.clientReason ?? 'client_unverified') }}</p>
      <p>{{ L.unverifiedHelp }}</p>
    </div>
  </div>
</template>

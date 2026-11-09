<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useJobPoll } from '@/composables/useJobPoll'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { CANCEL_RUN } from '@/utils/labels'
import { jobMessage } from '@/utils/messages'
import { problemMessage } from '@/utils/problem'

// Avbryt körning (#488 part 3; admin design §4 `POST /api/runs/{id}/cancel`, engine design §10.1): a run that is running
// or waiting for a deletion decision can be stopped. The confirm queues the `cancel-run` command; the button waits for
// that job, after which the run is `cancelled`, and the page reloads. An ended run answers `run_not_active` in the dialog.
const props = defineProps<{ runId: string; state: string }>()
const emit = defineEmits<{ (e: 'cancelled'): void }>()
const auth = useAuthStore()
const ui = useUiStore()
const poll = useJobPoll<unknown>()
const confirming = ref(false)
const failure = ref<string | null>(null)
const canCancel = computed(() => auth.hasAnyRole(['rag-platform-admin', 'rag-department-admin', 'rag-editor']))
const active = computed(() => props.state === 'running' || props.state === 'awaiting-deletion-confirmation')

async function cancel(): Promise<void> {
  failure.value = null
  const started = await api.runs.cancel(props.runId)
  poll.start(started.jobId)
}

watch(
  () => poll.job.value?.state,
  (state) => {
    if (state === 'succeeded') {
      ui.notify(CANCEL_RUN.done)
      emit('cancelled')
    } else if (state && state !== 'queued' && state !== 'running') {
      failure.value = CANCEL_RUN.failed(jobMessage(poll.job.value?.lastError ?? state))
    }
  },
)
watch(poll.error, (e) => {
  if (e) failure.value = problemMessage(e)
})
</script>

<template>
  <template v-if="canCancel && active">
    <UiButton variant="danger" icon="block" :loading="poll.polling.value" data-test="cancel-run" @click="confirming = true">
      {{ poll.polling.value ? CANCEL_RUN.cancelling : CANCEL_RUN.action }}
    </UiButton>
    <InlineError v-if="failure" :message="failure" data-test="cancel-run-error" />
    <ConfirmDialog
      v-model:open="confirming" :title="CANCEL_RUN.title" :consequence="CANCEL_RUN.consequence" :confirm-label="CANCEL_RUN.action"
      :cancel-label="CANCEL_RUN.keep" danger :action="cancel"
    />
  </template>
</template>

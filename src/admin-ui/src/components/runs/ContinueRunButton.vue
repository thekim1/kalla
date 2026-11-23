<script setup lang="ts">
import { computed, ref } from 'vue'
import { api } from '@/api'
import type { SyncStarted } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { CONTINUE_RUN } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'

// Continue (#804; `POST /api/runs/{id}/continue`): the latest crawl run that ended with pages left or failed goes on from
// them, with their links, instead of starting again from the start addresses. Shown only when the run says it can be
// continued (`canContinue`) and to the roles that may sync. A sync already queued or running is answered instead.
const props = defineProps<{ runId: string; canContinue: boolean }>()
const emit = defineEmits<{ (e: 'started', started: SyncStarted): void }>()
const auth = useAuthStore()
const ui = useUiStore()
const busy = ref(false)
const failure = ref<unknown>(null)
const canEdit = computed(() => auth.can('edit'))

async function start(): Promise<void> {
  busy.value = true
  failure.value = null
  try {
    const started = await api.runs.continue(props.runId)
    ui.notify(started.alreadyRunning ? CONTINUE_RUN.alreadyRunning : CONTINUE_RUN.started)
    emit('started', started)
  } catch (e) {
    failure.value = e
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <template v-if="canEdit && canContinue">
    <UiButton icon="play_arrow" :loading="busy" :title="CONTINUE_RUN.hint" data-test="continue-run" @click="start">{{ CONTINUE_RUN.action }}</UiButton>
    <InlineError v-if="failure" :message="problemMessage(failure)" data-test="continue-run-error" />
  </template>
</template>

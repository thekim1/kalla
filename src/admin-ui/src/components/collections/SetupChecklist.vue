<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink, type RouteLocationRaw } from 'vue-router'
import type { CollectionSetup } from '@/api/types'
import AppIcon from '@/components/ui/AppIcon.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { SETUP } from '@/utils/labels'

// Kom igång (admin design §5.4, §5.5.2): the journey's steps from the source to Open WebUI, each linking to the page
// that does it, computed from state by the API. The caller hides the card once `done`.
const props = defineProps<{ setup: CollectionSetup; showName?: boolean }>()

type State = 'done' | 'active' | 'todo'
interface Step { key: string; label: string; state: State; to: RouteLocationRaw }

const steps = computed<Step[]>(() => {
  const s = props.setup
  const tab = (name: string): RouteLocationRaw => ({ name: 'collection', params: { id: s.collectionId }, query: { tab: name } })
  const assistant: RouteLocationRaw = s.assistantId ? { name: 'assistant', params: { id: s.assistantId } } : { name: 'assistants' }
  const state = (done: boolean, active = false): State => (done ? 'done' : active ? 'active' : 'todo')
  return [
    { key: 'source', label: s.sources ? SETUP.steps.sourceDone(s.sources) : SETUP.steps.source, state: state(s.sources > 0), to: tab('sources') },
    {
      key: 'sync', label: s.firstSyncDone ? SETUP.steps.syncDone : s.syncRunning ? SETUP.steps.syncRunning : SETUP.steps.sync,
      state: state(s.firstSyncDone, s.syncRunning), to: tab('sources'),
    },
    { key: 'questions', label: s.testQuestions ? SETUP.steps.questionsDone(s.testQuestions) : SETUP.steps.questions, state: state(s.testQuestions > 0), to: tab('evaluation') },
    {
      key: 'evaluation', label: s.evaluated ? SETUP.steps.evaluated : s.evaluating ? SETUP.steps.evaluating : SETUP.steps.evaluation,
      state: state(s.evaluated, s.evaluating), to: tab('evaluation'),
    },
    { key: 'publish', label: s.published ? SETUP.steps.published : SETUP.steps.publish, state: state(s.published), to: tab('overview') },
    { key: 'assistant', label: s.assistantId ? SETUP.steps.assistantDone : SETUP.steps.assistant, state: state(!!s.assistantId), to: assistant },
    {
      key: 'assistant-publish', label: s.assistantPublished ? SETUP.steps.assistantPublished : SETUP.steps.assistantPublish, state: state(s.assistantPublished),
      to: assistant,
    },
    { key: 'available', label: SETUP.steps.available, state: state(s.available), to: assistant },
  ]
})

const ICON: Record<State, string> = { done: 'check_circle', active: 'pending', todo: 'radio_button_unchecked' }
const STATE_TEXT: Record<State, string> = { done: SETUP.done, active: SETUP.inProgress, todo: SETUP.todo }
const TONE: Record<State, string> = { done: 'text-ok', active: 'text-accent', todo: 'text-fg-3' }
</script>

<template>
  <UiCard :title="showName ? SETUP.titleFor(setup.name) : SETUP.title" data-test="setup">
    <ol class="grid gap-2 sm:grid-cols-2" :aria-label="SETUP.listLabel">
      <li v-for="step in steps" :key="step.key" class="flex items-center gap-2" :data-test="`step-${step.key}`" :data-state="step.state">
        <AppIcon :name="ICON[step.state]" :size="20" :class="TONE[step.state]" />
        <RouterLink :to="step.to" class="underline-offset-2 hover:underline" :class="step.state === 'done' ? 'text-fg-2' : 'font-medium'">
          {{ step.label }}
        </RouterLink>
        <span class="sr-only">({{ STATE_TEXT[step.state] }})</span>
      </li>
    </ol>
  </UiCard>
</template>

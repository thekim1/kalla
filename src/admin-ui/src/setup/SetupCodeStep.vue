<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref } from 'vue'
import { api } from '@/api'
import { isApiError } from '@/api/client'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { SETUP_WIZARD } from '@/utils/labels'
import { problemMessage, retryAfterSeconds } from '@/utils/problem'

// Step 1 (admin design §5.11): the setup code gives the setup session (identity design §19, "Setup code"). The code lives
// in this component only and goes nowhere but the request body.
const emit = defineEmits<{ (e: 'started'): void; (e: 'closed'): void }>()
const L = SETUP_WIZARD.code

const code = ref('')
const error = ref<string | null>(null)
const sending = ref(false)
const waitUntil = ref(0)
const announced = ref('')
const now = ref(Date.now())
let clock: ReturnType<typeof setInterval> | undefined
const waitSeconds = computed(() => Math.max(0, Math.ceil((waitUntil.value - now.value) / 1000)))

function startWait(seconds: number): void {
  announced.value = L.wait(seconds)
  waitUntil.value = Date.now() + seconds * 1000
  now.value = Date.now()
  clearInterval(clock)
  clock = setInterval(() => {
    now.value = Date.now()
    if (waitSeconds.value > 0) return
    clearInterval(clock)
    announced.value = ''
  }, 1000)
}
onBeforeUnmount(() => clearInterval(clock))

/** The error is read with the field, which gets the focus (the forms' pattern, useFormErrors). */
function showError(message: string): void {
  error.value = message
  void nextTick(() => document.getElementById('setup-code')?.focus())
}

async function submit(): Promise<void> {
  if (sending.value || waitSeconds.value > 0) return
  const value = code.value.trim()
  if (!value) {
    showError(L.required)
    return
  }
  sending.value = true
  error.value = null
  try {
    await api.setup.session(value)
    code.value = ''
    emit('started')
  } catch (e) {
    if (isApiError(e) && e.status === 404) {
      emit('closed')
      return
    }
    const wait = retryAfterSeconds(e)
    if (wait !== null) startWait(wait)
    else showError(problemMessage(e))
  } finally {
    sending.value = false
  }
}
</script>

<template>
  <form class="grid gap-5" novalidate data-test="code-step" @submit.prevent="submit">
    <div class="grid gap-2 text-fg-2">
      <p>{{ L.where }}</p>
      <ul class="grid list-disc gap-1 pl-5">
        <li v-for="place in L.places" :key="place.form">
          <span class="font-medium text-fg">{{ place.form }}:</span>
          <code v-if="'command' in place" class="ml-1 font-mono text-small">{{ place.how }}</code>
          <span v-else class="ml-1">{{ place.how }}</span>
        </li>
      </ul>
    </div>
    <TextField
      id="setup-code" v-model="code" :label="L.label" :hint="L.hint" :error="error" mono name="setup-code" data-test="code"
    />
    <!-- The countdown is announced once, not every second. -->
    <p v-if="waitSeconds > 0" class="text-small text-warn" data-test="code-wait" aria-hidden="true">{{ L.wait(waitSeconds) }}</p>
    <p role="status" class="sr-only">{{ announced }}</p>
    <div>
      <UiButton variant="primary" type="submit" :loading="sending" :disabled="waitSeconds > 0" data-test="code-submit">{{ L.submit }}</UiButton>
    </div>
  </form>
</template>

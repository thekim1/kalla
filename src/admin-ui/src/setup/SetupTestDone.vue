<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { SETUP_WIZARD } from '@/utils/labels'
import { announceTestDone } from './testWindow'

// Where the test sign-in's window ends (`/setup?test=done`, identity design §19): it tells the wizard and closes. Without an
// opener (opened from the link, or the opener was cut off) it can't close itself, so it says so.
const L = SETUP_WIZARD.testDone
const orphan = ref(false)

onMounted(() => {
  if (announceTestDone()) window.close()
  else orphan.value = true
})
</script>

<template>
  <div class="grid gap-2 py-10 text-center" data-test="test-done">
    <h1 id="page-title" tabindex="-1" class="text-title-2 outline-none">{{ L.title }}</h1>
    <p v-if="orphan" role="status" class="text-fg-2" data-test="test-done-close">{{ L.close }}</p>
  </div>
</template>

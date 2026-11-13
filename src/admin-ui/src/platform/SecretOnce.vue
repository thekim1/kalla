<script setup lang="ts">
import { PLATFORM } from '@/utils/labels'
import CopyValue from './CopyValue.vue'

// A new, rotated or revealed key's secret, shown once with a copy button (identity design §6.4, admin design §5.8).
defineProps<{ secret: string }>()
</script>

<template>
  <div class="grid gap-3" data-test="secret">
    <p class="font-medium">{{ PLATFORM.form.secretOnce }}</p>
    <CopyValue :value="secret" block />
  </div>
</template>

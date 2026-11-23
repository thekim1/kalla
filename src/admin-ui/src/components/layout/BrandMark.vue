<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useAuthStore } from '@/stores/auth'
import AppIcon from '@/components/ui/AppIcon.vue'

/**
 * The admin UI's mark: the organisation's logo when the gateway's admin console has one (the user document's `logoUrl`,
 * served by kalla-admin from this origin), otherwise the built-in icon. Decorative: the product and organisation names
 * beside it say the same.
 */
const props = withDefaults(defineProps<{ size?: 'sm' | 'md' | 'lg' }>(), { size: 'md' })
const auth = useAuthStore()
const logoUrl = computed(() => auth.user?.logoUrl ?? null)
const failed = ref(false)
watch(logoUrl, () => (failed.value = false))
const box = computed(() => ({ sm: 'h-6 w-6', md: 'h-9 w-9', lg: 'h-11 w-11' })[props.size])
// A logo keeps the mark's height and its own shape, so a wide wordmark is not shrunk into a square.
const logoBox = computed(() => ({ sm: 'h-6 max-w-24', md: 'h-9 max-w-36', lg: 'h-11 max-w-48' })[props.size])
</script>

<template>
  <img
    v-if="logoUrl && !failed"
    :src="logoUrl"
    alt=""
    :class="logoBox"
    class="w-auto shrink-0 rounded-control object-contain"
    data-test="brand-logo"
    @error="failed = true"
  />
  <AppIcon v-else-if="size === 'sm'" name="hub" aria-hidden="true" data-test="brand-icon" />
  <span v-else :class="box" class="flex shrink-0 items-center justify-center rounded-control bg-fg text-on-fg" aria-hidden="true" data-test="brand-icon">
    <AppIcon name="hub" />
  </span>
</template>

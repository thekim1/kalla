<script setup lang="ts">
import { onMounted } from 'vue'
import { RouterView, useRoute } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import AppShell from '@/components/layout/AppShell.vue'
import PublicLayout from '@/components/layout/PublicLayout.vue'
import SignIn from '@/components/layout/SignIn.vue'
import { APP } from '@/utils/labels'

const auth = useAuthStore()
const route = useRoute()
onMounted(() => void auth.ensureLoaded())
</script>

<template>
  <div v-if="auth.status === 'unknown' || auth.status === 'loading'" class="flex min-h-screen items-center justify-center text-small text-fg-3" role="status">{{ APP.loading }}</div>
  <PublicLayout v-else-if="route.meta.public"><RouterView /></PublicLayout>
  <SignIn v-else-if="auth.status === 'error'" failed />
  <SignIn v-else-if="!auth.isAuthenticated" />
  <AppShell v-else><RouterView /></AppShell>
</template>

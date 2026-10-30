<script setup lang="ts">
import { RouterLink } from 'vue-router'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { FORBIDDEN, PAGE } from '@/utils/labels'
import { useAuthStore } from '@/stores/auth'

// Adapted from the gateway: Swedish (admin design §5.6); a user without any rag-* role gets no way onwards.
const auth = useAuthStore()
</script>

<template>
  <PageHeader :title="PAGE.forbidden.title" :description="FORBIDDEN.body" />
  <RouterLink v-if="auth.hasAccess" v-slot="{ href, navigate }" :to="{ name: 'overview' }" custom>
    <UiButton variant="primary" icon="space_dashboard" :href="href" @click="navigate">{{ FORBIDDEN.home }}</UiButton>
  </RouterLink>
</template>

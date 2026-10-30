<script setup lang="ts">
import { RouterLink } from 'vue-router'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { NOT_FOUND, PAGE } from '@/utils/labels'
</script>

<template>
  <PageHeader :title="PAGE.notFound.title" :description="NOT_FOUND.body" />
  <RouterLink v-slot="{ href, navigate }" :to="{ name: 'overview' }" custom>
    <UiButton variant="primary" icon="space_dashboard" :href="href" @click="navigate">{{ NOT_FOUND.home }}</UiButton>
  </RouterLink>
</template>

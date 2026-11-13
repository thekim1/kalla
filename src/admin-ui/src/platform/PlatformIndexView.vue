<script setup lang="ts">
import { RouterLink } from 'vue-router'
import AppIcon from '@/components/ui/AppIcon.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import { PAGE, PLATFORM } from '@/utils/labels'
import { platformEntries } from './resources'
</script>

<template>
  <PageHeader :title="PAGE.platform.title" :description="PLATFORM.lead" />
  <ul class="grid gap-3 sm:grid-cols-2">
    <li v-for="r in platformEntries" :key="r.id">
      <RouterLink
        :to="r.to" :data-test="`resource-${r.id}`"
        class="flex gap-3 rounded-card border border-border bg-surface p-4 hover:bg-sunken focus-visible:outline-2"
      >
        <AppIcon :name="r.icon" />
        <span>
          <span class="block font-medium">{{ r.label }}</span>
          <span class="block text-small text-fg-2">{{ r.description }}</span>
        </span>
      </RouterLink>
    </li>
  </ul>
</template>

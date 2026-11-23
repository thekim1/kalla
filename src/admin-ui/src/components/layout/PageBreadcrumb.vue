<script setup lang="ts">
import type { RouteLocationRaw } from 'vue-router'
import { SHELL } from '@/utils/labels'

export interface Crumb {
  label: string
  to: RouteLocationRaw
}

// Admin design §5.12 (Every page): a detail page has a breadcrumb above its title, so the way back is visible. Only the
// pages above this one; the title below names the page itself. APG Breadcrumb pattern: a labelled nav with a list.
defineProps<{ items: Crumb[] }>()
</script>

<template>
  <nav :aria-label="SHELL.breadcrumb" class="mb-2" data-test="breadcrumb">
    <ol class="m-0 flex list-none flex-wrap items-center gap-x-1 p-0 text-small text-fg-2">
      <li v-for="item in items" :key="item.label" class="flex items-center gap-x-1">
        <RouterLink :to="item.to" class="underline-offset-2 hover:text-fg hover:underline">{{ item.label }}</RouterLink>
        <span aria-hidden="true">/</span>
      </li>
    </ol>
  </nav>
</template>

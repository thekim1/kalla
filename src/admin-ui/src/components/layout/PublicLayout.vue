<script setup lang="ts">
import AppIcon from '@/components/ui/AppIcon.vue'
import { APP } from '@/utils/labels'

// The frame of a page open before sign-in (the setup wizard, admin design §5.11): the product's name and the page, no
// menu, no session.
</script>

<template>
  <div class="flex min-h-screen flex-col">
    <header class="flex items-center gap-3 px-6 py-4">
      <span class="flex h-9 w-9 items-center justify-center rounded-control bg-fg text-on-fg" aria-hidden="true"><AppIcon name="hub" /></span>
      <span class="text-heading">{{ APP.name }}</span>
    </header>
    <main id="main" tabindex="-1" class="flex flex-1 justify-center px-4 pb-10 outline-none">
      <div class="w-full max-w-3xl"><slot /></div>
    </main>
  </div>
</template>

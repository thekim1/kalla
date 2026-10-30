<script setup lang="ts">
import { APP, ROLE_LABEL, SHELL, THEME_LABEL } from '@/utils/labels'
import type { Role } from '@/api/types'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import { isNavItemActive, navGroups, mobileTabs, type NavItem } from '@/router/nav'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import AppIcon from '@/components/ui/AppIcon.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import ToastRegion from '@/components/ui/ToastRegion.vue'
import CommandPalette from './CommandPalette.vue'
import MobileMenu from './MobileMenu.vue'
import SessionExpiryDialog from './SessionExpiryDialog.vue'
import { initials } from '@/utils/format'

const auth = useAuthStore()
const ui = useUiStore()
const route = useRoute()
const paletteOpen = ref(false)
const menuOpen = ref(false)

const visible = (item: NavItem) => auth.canOpen(item)
const groups = computed(() =>
  navGroups.map((g) => ({ ...g, items: g.items.filter(visible) })).filter((g) => g.items.length > 0),
)
const tabs = computed(() => mobileTabs.filter(visible))

// Attention items arrive with the Översikt page (admin design §5.2); none in P0.
function badgeCount(item: NavItem): number {
  return item.badge === 'attention' ? 0 : 0
}
const updateAvailable = false

const themes = [
  { value: 'light' as const, label: THEME_LABEL.light },
  { value: 'dark' as const, label: THEME_LABEL.dark },
  { value: 'lumen' as const, label: THEME_LABEL.lumen },
]

const now = ref(Date.now())
const clock = setInterval(() => (now.value = Date.now()), 30_000)
onBeforeUnmount(() => clearInterval(clock))
const sessionLeft = computed(() => {
  const at = auth.sessionExpiresAt?.getTime()
  if (!at) return null
  const mins = Math.max(0, Math.floor((at - now.value) / 60_000))
  return SHELL.sessionLeft(mins)
})

/** The first rag-* role in Swedish, under the user's name. */
const roleLabel = computed(() => auth.roles.map((r) => ROLE_LABEL[r as Role]).find(Boolean) ?? '')

const isActive = (item: NavItem) => isNavItemActive(item, route.name)
/** On phones, Menu is highlighted when the open page is not one of the bottom tabs. */
const menuActive = computed(() => !tabs.value.some(isActive))
const menuBadge = computed(() => groups.value.some((g) => g.items.some((i) => !tabs.value.some((t) => t.name === i.name) && badgeCount(i) > 0)))

function onKey(event: KeyboardEvent): void {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
    event.preventDefault()
    menuOpen.value = false
    paletteOpen.value = !paletteOpen.value
  }
}
onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))

const signingOut = ref(false)
async function signOut(): Promise<void> {
  signingOut.value = true
  try {
    window.location.assign(await auth.logout())
  } finally {
    signingOut.value = false
  }
}
</script>

<template>
  <a
    href="#main"
    class="material-overlay fixed left-4 top-2 z-toast -translate-y-24 rounded-control px-4 py-2 text-small font-medium text-fg focus:translate-y-0"
  >{{ SHELL.skipToMain }}</a>

  <div class="flex min-h-screen">
    <aside
      class="sticky top-0 hidden h-screen bg-sidebar [backdrop-filter:var(--blur)] shrink-0 flex-col gap-4 overflow-hidden border-r border-border p-4 md:flex md:w-[72px] lg:w-sidebar lg:px-5"
    >
      <div class="flex items-center gap-3 px-1">
        <span class="flex h-9 w-9 shrink-0 items-center justify-center rounded-control bg-fg text-on-fg" aria-hidden="true">
          <AppIcon name="hub" />
        </span>
        <span class="hidden min-w-0 lg:block">
          <span class="block truncate text-body font-semibold text-fg">{{ APP.name }}</span>
          <span class="block truncate text-caption text-fg-3">{{ APP.organisation }}</span>
        </span>
      </div>

      <nav :aria-label="SHELL.mainNav" class="-mx-1 flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto px-1 max-lg:gap-3">
        <section
          v-for="(g, gi) in groups"
          :key="g.id"
          :aria-labelledby="`nav-${g.id}`"
          class="flex flex-col gap-0.5"
          :class="{ 'max-lg:border-t max-lg:border-border max-lg:pt-3': gi > 0 }"
        >
          <h2 :id="`nav-${g.id}`" class="px-2 pb-1 text-caption font-medium uppercase tracking-wide text-fg-3 max-lg:sr-only">{{ g.label }}</h2>
          <RouterLink
            v-for="item in g.items"
            :key="item.name"
            :to="{ name: item.name }"
            :aria-current="isActive(item) ? 'page' : undefined"
            :title="item.description ? `${item.label}: ${item.description}` : item.label"
            class="relative flex h-ctl items-center gap-3 rounded-control px-2.5 text-small text-fg-2 hover:bg-hover hover:text-fg aria-[current=page]:bg-surface aria-[current=page]:font-semibold aria-[current=page]:text-fg aria-[current=page]:shadow-1 max-lg:justify-center"
          >
            <AppIcon :name="item.icon" :filled="isActive(item)" />
            <span class="hidden flex-1 truncate lg:inline">{{ item.label }}</span>
            <span
              v-if="badgeCount(item) > 0"
              class="tabular hidden h-badge min-w-[22px] items-center justify-center rounded-chip bg-danger px-1.5 text-caption font-semibold text-danger-fg lg:flex"
            >
              {{ badgeCount(item) }}<span class="sr-only">{{ SHELL.needAttention }}</span>
            </span>
          </RouterLink>
        </section>
      </nav>

      <div class="flex flex-col gap-3">
        <div class="hidden lg:block">
          <SegmentedControl
            :model-value="ui.theme"
            :options="themes"
            :label="SHELL.theme"
            mode="radio"
            full
            @update:model-value="ui.setTheme($event)"
          />
        </div>
        <div class="flex items-center gap-2.5 rounded-[12px] bg-surface p-2 shadow-1 max-lg:flex-col max-lg:px-1">
          <span class="flex size-8 shrink-0 items-center justify-center rounded-full bg-accent-soft text-caption font-semibold text-accent-ink" aria-hidden="true">
            {{ initials(auth.user?.name ?? auth.user?.email ?? '?') }}
          </span>
          <span class="hidden min-w-0 flex-1 leading-4 lg:block">
            <span class="block truncate text-[13px] font-medium text-fg" lang="sv">{{ auth.user?.name ?? auth.user?.email }}</span>
            <span class="block truncate text-caption text-fg-3">{{ roleLabel }}</span>
          </span>
          <button
            type="button"
            class="flex size-8 shrink-0 items-center justify-center rounded-[8px] text-fg-2 hover:bg-hover hover:text-fg"
            :aria-disabled="signingOut"
            @click="signOut"
          >
            <AppIcon name="logout" />
            <span class="sr-only">{{ SHELL.signOut }}</span>
          </button>
        </div>
      </div>
    </aside>

    <div class="flex min-w-0 flex-1 flex-col">
      <header class="bg-canvas-fixed sticky top-0 z-topbar flex h-topbar items-center gap-3 border-b border-border px-4 md:px-10">
        <div class="flex items-center gap-2 md:hidden">
          <AppIcon name="hub" />
          <span class="text-body font-semibold">{{ APP.name }}</span>
        </div>
        <button
          type="button"
          class="ml-auto flex h-ctl items-center gap-2 rounded-control bg-surface px-3 text-small text-fg-3 shadow-1 hover:text-fg max-md:size-10 max-md:justify-center max-md:px-0 md:ml-0 md:w-80"
          @click="paletteOpen = true"
        >
          <AppIcon name="search" />
          <span class="hidden flex-1 text-left md:inline">{{ SHELL.search }}</span>
          <kbd class="hidden rounded-chip px-1.5 text-caption md:inline" aria-hidden="true">{{ SHELL.searchShortcut }}</kbd>
          <span class="sr-only md:hidden">{{ SHELL.search }}</span>
        </button>
        <div class="ml-auto hidden items-center gap-3 md:flex">
          <span
            class="inline-flex h-[26px] items-center gap-1.5 whitespace-nowrap rounded-[13px] px-2.5 text-caption font-medium"
            :class="auth.isDevelopment ? 'bg-warn-soft text-warn' : 'bg-ok-soft text-ok'"
          ><span class="size-1.5 rounded-full" :class="auth.isDevelopment ? 'bg-warn' : 'bg-ok'" aria-hidden="true"></span>{{ auth.isDevelopment ? SHELL.development : SHELL.production }}</span>
          <span v-if="sessionLeft" class="tabular whitespace-nowrap text-small text-fg-3">{{ sessionLeft }}</span>
        </div>
      </header>

      <main id="main" tabindex="-1" class="mx-auto w-full max-w-content px-4 pb-[calc(96px+env(safe-area-inset-bottom))] pt-6 outline-none md:px-10 md:pb-16 md:pt-9">
        <slot />
      </main>
    </div>
  </div>

  <nav
    :aria-label="SHELL.quickAccess"
    class="material fixed inset-x-0 bottom-0 z-topbar grid border-t border-border pb-[env(safe-area-inset-bottom)] md:hidden"
    :style="{ gridTemplateColumns: `repeat(${tabs.length + 1}, 1fr)` }"
  >
    <RouterLink
      v-for="item in tabs"
      :key="item.name"
      :to="{ name: item.name }"
      :aria-current="isActive(item) ? 'page' : undefined"
      class="relative flex h-16 flex-col items-center justify-center gap-0.5 text-caption text-fg-2 aria-[current=page]:font-semibold aria-[current=page]:text-fg"
    >
      <AppIcon :name="item.icon" :filled="isActive(item)" />
      {{ item.label }}
      <span v-if="badgeCount(item) > 0" class="absolute right-1/4 top-2 h-2 w-2 rounded-full bg-danger" aria-hidden="true" />
    </RouterLink>
    <button
      type="button"
      class="relative flex h-16 flex-col items-center justify-center gap-0.5 text-caption text-fg-2 aria-expanded:text-fg"
      :class="{ 'font-semibold text-fg': menuActive }"
      aria-haspopup="dialog"
      :aria-expanded="menuOpen"
      @click="menuOpen = true"
    >
      <AppIcon name="menu" :filled="menuActive" />
      {{ SHELL.menu }}
      <span v-if="menuBadge || updateAvailable" class="absolute right-1/4 top-2 h-2 w-2 rounded-full" :class="menuBadge ? 'bg-danger' : 'bg-accent'" aria-hidden="true" />
    </button>
  </nav>

  <MobileMenu
    v-model:open="menuOpen"
    :groups="groups"
    :badge-count="badgeCount"
    :update-available="updateAvailable"
    :themes="themes"
    :session-left="sessionLeft"
    :signing-out="signingOut"
    @search="paletteOpen = true"
    @sign-out="signOut"
  />
  <CommandPalette v-model:open="paletteOpen" />
  <SessionExpiryDialog />
  <ToastRegion />
</template>

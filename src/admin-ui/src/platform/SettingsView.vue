<script setup lang="ts">
import { computed, ref } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import PageHeader from '@/components/ui/PageHeader.vue'
import SelectField from '@/components/ui/SelectField.vue'
import { useUnsavedGuard } from '@/composables/useUnsavedGuard'
import { SETTINGS_PAGE } from '@/utils/labels'
import PlatformResourceView from './PlatformResourceView.vue'
import PromptTextsPanel from './PromptTextsPanel.vue'
import SettingsSectionForm from './SettingsSectionForm.vue'
import { SETTINGS_PAGE_SECTIONS, settingsGroups, settingsSection } from './settingsPage'

// The Settings page (admin design §5.10, platform admins): the sections in a list grouped by what they affect (a select on
// phones), the chosen section's form on the page. The address names the section (`/plattform/settings/chat`); without one,
// or with an unknown one, the first section opens.
const props = defineProps<{ section?: string }>()
const router = useRouter()
const groups = settingsGroups()
const current = computed(() => settingsSection(props.section) ?? SETTINGS_PAGE_SECTIONS[0]!)
const options = SETTINGS_PAGE_SECTIONS.map((s) => ({ value: s.id, label: s.label }))
// Leaving the open section with unsaved changes asks first (admin §5.12, *Saving*): another section is another address,
// so the section list and the phone's picker ask through the route guard, as does leaving the page or the browser tab.
const dirty = ref(false)
useUnsavedGuard(() => dirty.value)

function go(id: string): void {
  void router.push({ name: 'platform-settings', params: { section: id } })
}
</script>

<template>
  <PageHeader :title="SETTINGS_PAGE.title" :description="SETTINGS_PAGE.lead" />
  <div class="grid gap-8 md:grid-cols-[14rem_1fr]">
    <nav :aria-label="SETTINGS_PAGE.sections" class="max-md:hidden" data-test="settings-sections">
      <section v-for="g in groups" :key="g.id" class="mb-5" :aria-labelledby="`settings-group-${g.id}`">
        <h2 :id="`settings-group-${g.id}`" class="px-2 pb-1 text-caption font-medium uppercase tracking-wide text-fg-3">{{ g.label }}</h2>
        <ul class="grid gap-0.5">
          <li v-for="s in g.sections" :key="s.id">
            <RouterLink
              :to="{ name: 'platform-settings', params: { section: s.id } }" :aria-current="s.id === current.id ? 'page' : undefined"
              class="flex h-ctl items-center rounded-control px-2.5 text-small text-fg-2 hover:bg-hover hover:text-fg aria-[current=page]:bg-surface aria-[current=page]:font-semibold aria-[current=page]:text-fg aria-[current=page]:shadow-1"
              :data-test="`settings-link-${s.id}`"
            >
              {{ s.label }}
            </RouterLink>
          </li>
        </ul>
      </section>
    </nav>
    <div class="md:hidden">
      <SelectField id="settings-section" :model-value="current.id" :label="SETTINGS_PAGE.section" :options="options" @update:model-value="go" />
    </div>
    <section class="min-w-0" aria-labelledby="settings-section-title" data-test="settings-section">
      <h2 id="settings-section-title" class="text-title-3 font-semibold">{{ current.label }}</h2>
      <p class="mb-6 mt-1 text-fg-2">{{ SETTINGS_PAGE.descriptions[current.id] }}</p>
      <SettingsSectionForm v-if="current.kind === 'settings'" :key="current.id" :section="current.id" :label="current.label" @dirty="dirty = $event" />
      <PromptTextsPanel v-else-if="current.kind === 'prompt-texts'" @dirty="dirty = $event" />
      <PlatformResourceView v-else :key="current.id" :resource="current.id" embedded />
    </section>
  </div>
</template>

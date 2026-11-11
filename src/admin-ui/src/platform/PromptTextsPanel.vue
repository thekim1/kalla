<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { http } from '@/api/client'
import InlineError from '@/components/ui/InlineError.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useUiStore } from '@/stores/ui'
import { formatNumber } from '@/utils/format'
import { PROMPT_TEXTS as P } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'

// The Settings page's Prompt texts panel (admin design §4.5; chat design §7.3; Decisions "Chat prompt texts configurable"):
// every prompt the platform sends, in order, from GET /api/settings/chat/prompts. Editable texts start from their saved value
// or default and have Reset to default; locked lines are read-only. A save writes only the texts that differ from their
// default into Settings.Chat.prompts (V38 on the server), keeping the chat section's other members.

interface PromptTextLine {
  member: string | null
  text: string
  value: string | null
  maxLength: number | null
  placeholder: string | null
  oneLine: boolean
}

interface PromptTextSection {
  id: string
  lines: PromptTextLine[]
}

const ui = useUiStore()
const sections = ref<PromptTextSection[]>([])
const values = ref<Record<string, string>>({})
const loadError = ref<unknown>(null)
const saveError = ref<unknown>(null)
const saving = ref(false)
const loaded = ref(false)

const defaults = computed(() =>
  Object.fromEntries(sections.value.flatMap((s) => s.lines).filter((l) => l.member).map((l) => [l.member!, l.text])),
)

async function load(): Promise<void> {
  loadError.value = null
  try {
    sections.value = await http.get<PromptTextSection[]>('/api/settings/chat/prompts')
    values.value = Object.fromEntries(sections.value.flatMap((s) => s.lines).filter((l) => l.member).map((l) => [l.member!, l.value ?? l.text]))
  } catch (e) {
    loadError.value = e
  } finally {
    loaded.value = true
  }
}

function hint(line: PromptTextLine): string {
  return [line.placeholder ? P.placeholders[line.placeholder] : undefined, line.maxLength ? P.maxLength(formatNumber(line.maxLength)) : undefined]
    .filter(Boolean)
    .join(' ')
}

const edited = (member: string) => values.value[member] !== defaults.value[member]

/** The changed texts as Settings.Chat.prompts: nested by the dots of their member paths; texts at their default are left out. */
function prompts(): Record<string, unknown> {
  const result: Record<string, unknown> = {}
  for (const [member, text] of Object.entries(values.value)) {
    if (text.trim() === '' || text === defaults.value[member]) continue
    const parts = member.split('.')
    let node = result
    for (const part of parts.slice(0, -1)) node = (node[part] ??= {}) as Record<string, unknown>
    node[parts[parts.length - 1]!] = text
  }
  return result
}

async function save(): Promise<void> {
  saving.value = true
  saveError.value = null
  try {
    const chat = await http.get<{ value: Record<string, unknown>; version: string }>('/api/settings/chat')
    const value = { ...chat.value }
    const changed = prompts()
    if (Object.keys(changed).length > 0) value.prompts = changed
    else delete value.prompts
    await http.put('/api/settings/chat', { value, version: chat.version })
    ui.notify(P.saved)
    await load()
  } catch (e) {
    saveError.value = e
  } finally {
    saving.value = false
  }
}

onMounted(load)
</script>

<template>
  <UiCard :title="P.title" heading-id="prompt-texts-title" data-test="prompt-texts">
    <p class="mb-2 text-fg-2">{{ P.lead }}</p>
    <p class="mb-4 font-medium" data-test="prompt-texts-note">{{ P.note }}</p>
    <InlineError v-if="loadError" :message="problemMessage(loadError)" class="mb-4" />
    <p v-if="!loaded">…</p>
    <form v-else-if="sections.length" class="grid gap-6" novalidate @submit.prevent="save">
      <section v-for="section in sections" :key="section.id" class="grid gap-3" :data-test="`prompt-section-${section.id}`">
        <h3 class="font-medium">{{ P.sections[section.id] ?? section.id }}</h3>
        <template v-for="(line, i) in section.lines" :key="line.member ?? `${section.id}-${i}`">
          <div v-if="line.member" class="grid gap-1" :data-test="`prompt-${line.member}`">
            <TextAreaField
              :id="`prompt-${line.member.replaceAll('.', '-')}`" v-model="values[line.member]!" :label="P.members[line.member] ?? line.member"
              :hint="hint(line)" :rows="line.oneLine ? 1 : Math.min(8, Math.max(2, Math.ceil(line.text.length / 90) + 1))"
            />
            <div class="flex items-center gap-3">
              <UiBadge v-if="edited(line.member)" tone="accent" :label="P.edited" />
              <UiButton
                v-if="edited(line.member)" variant="quiet" :data-test="`prompt-reset-${line.member}`"
                @click="values[line.member] = defaults[line.member]!"
              >
                {{ P.reset }}
              </UiButton>
            </div>
          </div>
          <p v-else class="flex gap-2 rounded-card bg-sunken p-3 text-small text-fg-2" data-test="prompt-locked">
            <UiBadge tone="neutral" :label="P.locked" />
            <span class="whitespace-pre-wrap">{{ line.text }}</span>
          </p>
        </template>
      </section>
      <InlineError v-if="saveError" :message="problemMessage(saveError)" />
      <div><UiButton variant="primary" type="submit" :loading="saving" data-test="prompt-texts-save">{{ P.save }}</UiButton></div>
    </form>
  </UiCard>
</template>

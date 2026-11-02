<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { Assistant, Department, Level } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { ASSISTANTS, COMMON, LEVEL_HELP, LEVEL_LABEL } from '@/utils/labels'

// Skapa assistent (admin design §5.4 step 9): name, department, level, description and contact (V34 needs both to publish).
const props = defineProps<{ open: boolean; departments: Department[] }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'created', assistant: Assistant): void }>()

const LEVELS: Level[] = ['public', 'internal', 'sensitive']
const name = ref('')
const departmentId = ref('')
const level = ref<Level | ''>('')
const description = ref('')
const contactName = ref('')
const contactEmail = ref('')
const saving = ref(false)
const form = useFormErrors({ name: 'assistant-name', departmentId: 'assistant-department', classification: 'assistant-level-public' })
const departmentOptions = computed(() => props.departments.map((d) => ({ value: d.id, label: d.name })))

watch(
  () => props.open,
  (open) => {
    if (!open) return
    name.value = ''
    level.value = ''
    description.value = ''
    contactName.value = ''
    contactEmail.value = ''
    departmentId.value = props.departments.length === 1 ? props.departments[0]!.id : ''
    form.clear()
  },
)

async function save(): Promise<void> {
  if (!form.validate({ name: !name.value.trim() && ASSISTANTS.nameRequired, classification: !level.value && ASSISTANTS.levelRequired })) return
  saving.value = true
  try {
    const created = await api.assistants.create({
      name: name.value.trim(), departmentId: departmentId.value, classification: level.value as Level,
      description: description.value.trim() || null, contactName: contactName.value.trim() || null, contactEmail: contactEmail.value.trim() || null,
    })
    emit('created', created)
    emit('update:open', false)
  } catch (e) {
    form.applyServerError(e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UiDrawer :open="open" :title="ASSISTANTS.create" @update:open="emit('update:open', $event)">
    <form id="assistant-create" class="grid gap-5" novalidate @submit.prevent="save">
      <TextField id="assistant-name" v-model="name" :label="ASSISTANTS.name" :error="form.errors.name" autocomplete="off" />
      <SelectField id="assistant-department" v-model="departmentId" :label="ASSISTANTS.department" :options="departmentOptions" :error="form.errors.departmentId" />
      <fieldset class="grid gap-2">
        <legend class="mb-1 font-medium">{{ ASSISTANTS.level }}</legend>
        <label v-for="l in LEVELS" :key="l" class="flex cursor-pointer gap-3 rounded-card border border-border p-3">
          <input :id="`assistant-level-${l}`" v-model="level" type="radio" name="assistant-level" :value="l" class="mt-1" />
          <span><span class="block font-medium">{{ LEVEL_LABEL[l] }}</span><span class="block text-small text-fg-2">{{ LEVEL_HELP[l] }}</span></span>
        </label>
        <p v-if="form.errors.classification" class="text-small text-danger">{{ form.errors.classification }}</p>
      </fieldset>
      <TextAreaField id="assistant-description" v-model="description" :label="ASSISTANTS.description" :hint="ASSISTANTS.descriptionHint" />
      <TextField id="assistant-contact" v-model="contactName" :label="ASSISTANTS.contactName" />
      <TextField id="assistant-email" v-model="contactEmail" :label="ASSISTANTS.contactEmail" type="email" inputmode="email" />
      <InlineError v-if="form.formError.value" :message="form.formError.value" />
    </form>
    <template #footer>
      <UiButton variant="primary" type="submit" form="assistant-create" :loading="saving" data-test="save">{{ ASSISTANTS.create }}</UiButton>
      <UiButton variant="quiet" @click="emit('update:open', false)">{{ COMMON.cancel }}</UiButton>
    </template>
  </UiDrawer>
</template>

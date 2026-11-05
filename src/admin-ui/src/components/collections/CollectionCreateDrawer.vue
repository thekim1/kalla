<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { Collection, Department, Level } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { useAuthStore } from '@/stores/auth'
import { COLLECTIONS, COMMON, LEVEL_HELP, LEVEL_LABEL } from '@/utils/labels'

// Admin design §5.4 step 2: name, department, contact (the creator by default) and the level as three explained options
// with none preselected ("Vem får läsa innehållet?"). No team field: the API makes the creator's first team in the
// department the owner team (Decisions "Organisation"). The slug is made from the name by the API.
const props = defineProps<{ open: boolean; departments: Department[] }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'created', collection: Collection): void }>()

const LEVELS: Level[] = ['public', 'internal', 'sensitive']
const name = ref('')
const departmentId = ref('')
const contactName = ref('')
const contactEmail = ref('')
const auth = useAuthStore()
const level = ref<Level | ''>('')
const saving = ref(false)
const form = useFormErrors({
  name: 'collection-name', departmentId: 'collection-department', classification: 'collection-level-public', contactName: 'collection-contact',
  contactEmail: 'collection-email',
})

const departmentOptions = computed(() => props.departments.map((d) => ({ value: d.id, label: d.name })))

watch(
  () => props.open,
  (open) => {
    if (!open) return
    name.value = ''
    level.value = ''
    contactName.value = auth.user?.name ?? ''
    contactEmail.value = auth.user?.email ?? ''
    departmentId.value = props.departments.length === 1 ? props.departments[0]!.id : ''
    form.clear()
  },
)

async function save(): Promise<void> {
  if (!form.validate({
    name: !name.value.trim() && COLLECTIONS.nameRequired,
    departmentId: !departmentId.value && COLLECTIONS.departmentRequired,
    classification: !level.value && COLLECTIONS.levelRequired,
  })) return
  saving.value = true
  try {
    const created = await api.collections.create({
      name: name.value.trim(), departmentId: departmentId.value, classification: level.value as Level,
      contactName: contactName.value.trim() || null, contactEmail: contactEmail.value.trim() || null,
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
  <UiDrawer :open="open" :title="COLLECTIONS.create" @update:open="emit('update:open', $event)">
    <form id="collection-create" class="grid gap-5" novalidate @submit.prevent="save">
      <TextField id="collection-name" v-model="name" :label="COLLECTIONS.name" :error="form.errors.name" autocomplete="off" />
      <SelectField
        id="collection-department" v-model="departmentId" :label="COLLECTIONS.department" :options="departmentOptions"
        :error="form.errors.departmentId" :placeholder="COLLECTIONS.departmentRequired"
      />
      <TextField id="collection-contact" v-model="contactName" :label="COLLECTIONS.contactName" :hint="COLLECTIONS.contactHint" :error="form.errors.contactName" autocomplete="name" />
      <TextField id="collection-email" v-model="contactEmail" :label="COLLECTIONS.contactEmail" type="email" inputmode="email" autocomplete="email" :error="form.errors.contactEmail" />
      <fieldset class="grid gap-2" :aria-describedby="form.errors.classification ? 'collection-level-error' : undefined">
        <legend class="mb-1 font-medium">{{ COLLECTIONS.levelQuestion }}</legend>
        <label v-for="l in LEVELS" :key="l" class="flex cursor-pointer gap-3 rounded-card border border-border p-3" :data-test="`level-${l}`">
          <input :id="`collection-level-${l}`" v-model="level" type="radio" name="classification" :value="l" class="mt-1" />
          <span>
            <span class="block font-medium">{{ LEVEL_LABEL[l] }}</span>
            <span class="block text-small text-fg-2">{{ LEVEL_HELP[l] }}</span>
          </span>
        </label>
        <p v-if="form.errors.classification" id="collection-level-error" class="text-small text-danger">{{ form.errors.classification }}</p>
      </fieldset>
      <InlineError v-if="form.formError.value" :message="form.formError.value" />
    </form>
    <template #footer>
      <UiButton variant="primary" type="submit" form="collection-create" :loading="saving" data-test="save">{{ COLLECTIONS.create }}</UiButton>
      <UiButton variant="quiet" @click="emit('update:open', false)">{{ COMMON.cancel }}</UiButton>
    </template>
  </UiDrawer>
</template>

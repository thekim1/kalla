<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { api } from '@/api'
import type { Collection, Department, Level } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { createUploadSource } from '@/components/files/files'
import { useAuthStore } from '@/stores/auth'
import { COLLECTIONS, COMMON, LEVEL_HELP, LEVEL_LABEL } from '@/utils/labels'

// Admin design §5.4 step 2, §5.12 *Drawers*: in the order of the decision, name → what goes in → who may read it (three
// explained levels, none preselected) → department and contact, the contact pre-filled with the creator and folded into
// one line ("Contact: Anna Andersson, anna@… · Change"). No team field: the API makes the creator's first team in the
// department the owner team (Decisions "Organisation"). The slug is made from the name by the API. What goes in
// (§5.5.7), also with none preselected: *Files I upload* creates the knowledge base and its upload source (named Files)
// in the same submit; when the source fails the knowledge base still exists and `created` carries the error, so the page
// that opens can offer Add files.
type Content = 'files' | 'web'
const props = defineProps<{ open: boolean; departments: Department[] }>()
const emit = defineEmits<{
  (e: 'update:open', value: boolean): void
  (e: 'created', collection: Collection, content: Content, filesError: unknown): void
}>()

const LEVELS: Level[] = ['public', 'internal', 'sensitive']
const CONTENTS: Content[] = ['files', 'web']
const name = ref('')
const departmentId = ref('')
const contactName = ref('')
const contactEmail = ref('')
const auth = useAuthStore()
const level = ref<Level | ''>('')
const content = ref<Content | ''>('')
const saving = ref(false)
/** The contact fields, unfolded by Change, when there is no one to pre-fill, or when the server refuses one of them. */
const editingContact = ref(false)
const contactLine = computed(() => [contactName.value.trim(), contactEmail.value.trim()].filter(Boolean).join(', '))
const form = useFormErrors({
  name: 'collection-name', departmentId: 'collection-department', classification: 'collection-level-public', contactName: 'collection-contact',
  contactEmail: 'collection-email', content: 'collection-content-files',
})

const departmentOptions = computed(() => props.departments.map((d) => ({ value: d.id, label: d.name })))

watch(
  () => props.open,
  (open) => {
    if (!open) return
    name.value = ''
    level.value = ''
    content.value = ''
    contactName.value = auth.user?.name ?? ''
    contactEmail.value = auth.user?.email ?? ''
    departmentId.value = props.departments.length === 1 ? props.departments[0]!.id : ''
    editingContact.value = !contactLine.value
    form.clear()
  },
)

async function save(): Promise<void> {
  if (!form.validate({
    name: !name.value.trim() && COLLECTIONS.nameRequired,
    departmentId: !departmentId.value && COLLECTIONS.departmentRequired,
    classification: !level.value && COLLECTIONS.levelRequired,
    content: !content.value && COLLECTIONS.contentRequired,
  })) return
  saving.value = true
  try {
    const created = await api.collections.create({
      name: name.value.trim(), departmentId: departmentId.value, classification: level.value as Level,
      contactName: contactName.value.trim() || null, contactEmail: contactEmail.value.trim() || null,
    })
    // The knowledge base exists from here: a failed source does not undo it.
    const filesError = content.value === 'files' ? await createUploadSource(created).then(() => null, (e: unknown) => e) : null
    emit('created', created, content.value as Content, filesError)
    emit('update:open', false)
  } catch (e) {
    form.applyServerError(e)
    // A refused contact field: unfold the fields and focus the first refused one.
    if (form.errors.contactName || form.errors.contactEmail) {
      editingContact.value = true
      await nextTick()
      document.getElementById(form.errors.contactName ? 'collection-contact' : 'collection-email')?.focus()
    }
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UiDrawer :open="open" :title="COLLECTIONS.create" @update:open="emit('update:open', $event)">
    <form id="collection-create" class="grid gap-5" novalidate @submit.prevent="save">
      <TextField id="collection-name" v-model="name" :label="COLLECTIONS.name" :error="form.errors.name" autocomplete="off" />
      <fieldset class="grid gap-2" :aria-describedby="form.errors.content ? 'collection-content-error' : undefined">
        <legend class="mb-1 font-medium">{{ COLLECTIONS.contentQuestion }}</legend>
        <label v-for="c in CONTENTS" :key="c" class="flex cursor-pointer gap-3 rounded-card border border-border p-3 has-checked:bg-accent-soft has-checked:shadow-inset-selected" :data-test="`content-${c}`">
          <input :id="`collection-content-${c}`" v-model="content" type="radio" name="content" :value="c" class="mt-1" />
          <span>
            <span class="block font-medium">{{ COLLECTIONS.content[c] }}</span>
            <span class="block text-small text-fg-2">{{ COLLECTIONS.contentHelp[c] }}</span>
          </span>
        </label>
        <p v-if="form.errors.content" id="collection-content-error" class="text-small text-danger">{{ form.errors.content }}</p>
      </fieldset>
      <fieldset class="grid gap-2" :aria-describedby="form.errors.classification ? 'collection-level-error' : undefined">
        <legend class="mb-1 font-medium">{{ COLLECTIONS.levelQuestion }}</legend>
        <label v-for="l in LEVELS" :key="l" class="flex cursor-pointer gap-3 rounded-card border border-border p-3 has-checked:bg-accent-soft has-checked:shadow-inset-selected" :data-test="`level-${l}`">
          <input :id="`collection-level-${l}`" v-model="level" type="radio" name="classification" :value="l" class="mt-1" />
          <span>
            <span class="block font-medium">{{ LEVEL_LABEL[l] }}</span>
            <span class="block text-small text-fg-2">{{ LEVEL_HELP[l] }}</span>
          </span>
        </label>
        <p v-if="form.errors.classification" id="collection-level-error" class="text-small text-danger">{{ form.errors.classification }}</p>
      </fieldset>
      <SelectField
        id="collection-department" v-model="departmentId" :label="COLLECTIONS.department" :options="departmentOptions"
        :error="form.errors.departmentId" :placeholder="COLLECTIONS.departmentRequired"
      />
      <p v-if="!editingContact" class="flex flex-wrap items-center gap-x-2" data-test="contact-line">
        <span><span class="text-fg-2">{{ COLLECTIONS.contactLine }}:</span> {{ contactLine }}</span>
        <span aria-hidden="true" class="text-fg-3">·</span>
        <button type="button" class="text-accent-ink underline" :aria-label="COLLECTIONS.changeContact" data-test="change-contact" @click="editingContact = true">
          {{ COLLECTIONS.change }}
        </button>
      </p>
      <template v-else>
        <TextField id="collection-contact" v-model="contactName" :label="COLLECTIONS.contactName" :hint="COLLECTIONS.contactHint" :error="form.errors.contactName" />
        <TextField id="collection-email" v-model="contactEmail" :label="COLLECTIONS.contactEmail" type="email" inputmode="email" :error="form.errors.contactEmail" />
      </template>
      <InlineError v-if="form.formError.value" :message="form.formError.value" />
    </form>
    <template #footer>
      <UiButton variant="primary" type="submit" form="collection-create" :loading="saving" data-test="save">{{ COLLECTIONS.create }}</UiButton>
      <UiButton variant="quiet" @click="emit('update:open', false)">{{ COMMON.cancel }}</UiButton>
    </template>
  </UiDrawer>
</template>

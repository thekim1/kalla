<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { api } from '@/api'
import type { Collection, Level } from '@/api/types'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { COLLECTIONS, COMMON, EDIT_DRAWER, LEVEL_HELP, LEVEL_LABEL } from '@/utils/labels'
import { problemLines } from '@/utils/problem'

// Edit (admin design §5.5.2, #446): the knowledge base's own fields, the level included, saved through PUT /api/collections/{id}.
// The API's level checks (collection_level_conflict, assistant_below_collection) come back after Save, each with its affected objects.
const props = defineProps<{ open: boolean; collection: Collection }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'saved', collection: Collection): void }>()

const LEVELS: Level[] = ['public', 'internal', 'sensitive']
const name = ref('')
const level = ref<Level>('internal')
const contactName = ref('')
const contactEmail = ref('')
const saving = ref(false)
const refusals = ref<string[]>([])
const refusalBox = ref<HTMLElement | null>(null)
const form = useFormErrors({ name: 'collection-edit-name', contactName: 'collection-edit-contact', contactEmail: 'collection-edit-email' })

watch(
  () => props.open,
  (open) => {
    if (!open) return
    name.value = props.collection.name
    level.value = props.collection.classification
    contactName.value = props.collection.contactName ?? ''
    contactEmail.value = props.collection.contactEmail ?? ''
    refusals.value = []
    form.clear()
  },
  { immediate: true },
)

async function save(): Promise<void> {
  refusals.value = []
  if (!form.validate({ name: !name.value.trim() && COLLECTIONS.nameRequired })) return
  saving.value = true
  try {
    const saved = await api.collections.update(props.collection.id, {
      name: name.value.trim(), ownerTeamId: props.collection.ownerTeamId, classification: level.value,
      contactName: contactName.value.trim() || null, contactEmail: contactEmail.value.trim() || null, version: props.collection.version,
    })
    emit('saved', saved)
    emit('update:open', false)
  } catch (e) {
    form.applyServerError(e)
    if (form.formError.value) {
      form.formError.value = null
      refusals.value = problemLines(e)
      // The list sits under the fields: bring it into view (role=alert announces it).
      void nextTick(() => refusalBox.value?.scrollIntoView?.({ block: 'nearest' }))
    }
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UiDrawer :open="open" :title="EDIT_DRAWER.collectionTitle" @update:open="emit('update:open', $event)">
    <form id="collection-edit" class="grid gap-5" novalidate @submit.prevent="save">
      <TextField id="collection-edit-name" v-model="name" :label="COLLECTIONS.name" :error="form.errors.name" autocomplete="off" />
      <TextField id="collection-edit-contact" v-model="contactName" :label="COLLECTIONS.contactName" :error="form.errors.contactName" />
      <TextField id="collection-edit-email" v-model="contactEmail" :label="COLLECTIONS.contactEmail" type="email" inputmode="email" :error="form.errors.contactEmail" />
      <fieldset class="grid gap-2" aria-describedby="collection-edit-level-hint">
        <legend class="mb-1 font-medium">{{ COLLECTIONS.level }}</legend>
        <p id="collection-edit-level-hint" class="text-small text-fg-2">{{ EDIT_DRAWER.levelHint }}</p>
        <label v-for="l in LEVELS" :key="l" class="flex cursor-pointer gap-3 rounded-card border border-border p-3">
          <input :id="`collection-edit-level-${l}`" v-model="level" type="radio" name="collection-edit-level" :value="l" class="mt-1" />
          <span><span class="block font-medium">{{ LEVEL_LABEL[l] }}</span><span class="block text-small text-fg-2">{{ LEVEL_HELP[l] }}</span></span>
        </label>
      </fieldset>
      <div v-if="refusals.length" ref="refusalBox" role="alert" class="rounded-card border border-danger p-3 text-small text-danger" data-test="refusals">
        <p class="font-medium">{{ EDIT_DRAWER.refused }}</p>
        <ul class="ml-5 list-disc"><li v-for="r in refusals" :key="r">{{ r }}</li></ul>
      </div>
    </form>
    <template #footer>
      <UiButton variant="primary" type="submit" form="collection-edit" :loading="saving" data-test="save">{{ EDIT_DRAWER.save }}</UiButton>
      <UiButton variant="quiet" @click="emit('update:open', false)">{{ COMMON.cancel }}</UiButton>
    </template>
  </UiDrawer>
</template>

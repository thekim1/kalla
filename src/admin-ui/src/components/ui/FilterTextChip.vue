<script setup lang="ts">
import { computed, nextTick, ref, useId } from 'vue'
import { FILTERS } from '@/utils/labels'
import { NO_PASSWORD_MANAGER } from './noAutofill'

/**
 * A filter chip for free text (admin design §5.12 *Filters*): the Audit log's *Who*. Named for its filter ("Who:"), a
 * text field inside it with the `any` text as its placeholder; a set chip is coloured with an × of its own. The page
 * debounces what it sends. Built in Källa until Fors takes it.
 */
const props = defineProps<{ label: string; modelValue: string; any: string; hint?: string }>()
const emit = defineEmits<{ (e: 'update:modelValue', value: string): void }>()
const id = `chip-${useId()}`
const set = computed(() => props.modelValue.trim() !== '')
const input = ref<HTMLInputElement | null>(null)
/** The × goes with its own click: focus moves to the chip's field, not to the body (WCAG 2.4.3). */
function remove(): void {
  emit('update:modelValue', '')
  void nextTick(() => input.value?.focus())
}
</script>

<template>
  <span
    class="inline-flex h-ctl items-center rounded-full text-small max-md:h-11"
    :class="set ? 'bg-accent-soft text-accent-ink shadow-inset-selected' : 'bg-surface text-fg shadow-inset-input'"
    :data-test="`chip-${label}`"
  >
    <label :for="id" class="cursor-pointer pl-3 font-medium">{{ label }}:</label>
    <input
      :id="id" ref="input" :value="modelValue" type="text" :name="id" :placeholder="any" :title="hint" autocomplete="off" v-bind="NO_PASSWORD_MANAGER"
      class="h-full w-36 rounded-full bg-transparent px-1 text-small" :class="set ? '' : 'mr-2'" data-test="chip-input"
      @input="emit('update:modelValue', ($event.target as HTMLInputElement).value)"
    />
    <button
      v-if="set" type="button" class="mr-1 grid size-7 place-items-center rounded-full hover:bg-hover max-md:size-11"
      :aria-label="FILTERS.remove(label)" data-test="chip-remove" @click="remove"
    >
      <span aria-hidden="true">×</span>
    </button>
  </span>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { api } from '@/api'
import type { Assistant, Collection, Department, MoveImpact, MoveRequest } from '@/api/types'
import GatewayKeyPicker from '@/components/gateway/GatewayKeyPicker.vue'
import SelectField from '@/components/ui/SelectField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { COMMON, MOVE_DEPARTMENT } from '@/utils/labels'
import { problemLines } from '@/utils/problem'

// Change department (#811, admin design §3.2 "Changing the owner department"; platform admins only): the new department,
// an owner team and a gateway key of it, then Check (POST …/move/impact: the shares the move adds so every link keeps
// working, or the sources that block it) and Move with the impact's token. Any change to the fields drops the impact,
// so what is moved is what was shown.
const props = defineProps<{ open: boolean; kind: 'collection' | 'assistant'; id: string; departmentId: string; version: string }>()
const emit = defineEmits<{
  (e: 'update:open', value: boolean): void
  (e: 'moved-collection', collection: Collection, department: string): void
  (e: 'moved-assistant', assistant: Assistant, department: string): void
}>()

const departments = ref<Department[]>([])
const targetId = ref('')
const ownerTeamId = ref('')
const gatewayKeyId = ref<string | null>(null)
const impact = ref<MoveImpact | null>(null)
const busy = ref(false)
const refusals = ref<string[]>([])
const refusalBox = ref<HTMLElement | null>(null)

const targets = computed(() => departments.value.filter((d) => d.isActive && d.id !== props.departmentId))
const target = computed(() => targets.value.find((d) => d.id === targetId.value) ?? null)
const departmentOptions = computed(() => targets.value.map((d) => ({ value: d.id, label: d.name })))
// The owner team (main session's review on #811): one of the new department's active teams; its only team is preselected,
// with several the admin chooses one; none only when it has no team.
const teams = computed(() => (target.value?.teams ?? []).filter((t) => t.isActive))
const teamOptions = computed(() =>
  teams.value.length ? teams.value.map((t) => ({ value: t.id, label: t.name })) : [{ value: '', label: MOVE_DEPARTMENT.noTeam }],
)
const teamMissing = computed(() => teams.value.length > 1 && !ownerTeamId.value)

watch(
  () => props.open,
  async (open) => {
    if (!open) return
    targetId.value = ''
    ownerTeamId.value = ''
    gatewayKeyId.value = null
    impact.value = null
    refusals.value = []
    departments.value = await api.organisation.departments().catch(() => [])
  },
  { immediate: true },
)
watch(targetId, () => {
  ownerTeamId.value = teams.value.length === 1 ? teams.value[0]!.id : ''
  gatewayKeyId.value = null
})
watch([targetId, ownerTeamId, gatewayKeyId], () => {
  impact.value = null
  refusals.value = []
})

function body(token?: string): MoveRequest {
  return {
    departmentId: targetId.value, ownerTeamId: ownerTeamId.value || null, gatewayKeyId: gatewayKeyId.value, version: props.version,
    ...(token ? { impactToken: token } : {}),
  }
}

async function run(action: () => Promise<void>): Promise<void> {
  busy.value = true
  refusals.value = []
  try {
    await action()
  } catch (e) {
    refusals.value = problemLines(e)
    void nextTick(() => refusalBox.value?.scrollIntoView?.({ block: 'nearest' }))
  } finally {
    busy.value = false
  }
}

const check = () =>
  run(async () => {
    // Enter submits the form too: the same guard as the button.
    if (!target.value || teamMissing.value) return
    const calls = props.kind === 'collection' ? api.collections : api.assistants
    impact.value = await calls.moveImpact(props.id, body())
  })

const move = () =>
  run(async () => {
    const token = impact.value?.impactToken
    if (!token) return
    const department = target.value?.name ?? ''
    if (props.kind === 'collection') emit('moved-collection', await api.collections.move(props.id, body(token)), department)
    else emit('moved-assistant', await api.assistants.move(props.id, body(token)), department)
    emit('update:open', false)
  })
</script>

<template>
  <UiDrawer
    :open="open" :title="kind === 'collection' ? MOVE_DEPARTMENT.collectionTitle : MOVE_DEPARTMENT.assistantTitle"
    @update:open="emit('update:open', $event)"
  >
    <form id="move-department" class="grid gap-5" novalidate @submit.prevent="impact?.impactToken ? move() : check()">
      <p class="text-small text-fg-2">{{ MOVE_DEPARTMENT.intro }}</p>
      <SelectField id="move-department-target" v-model="targetId" :label="MOVE_DEPARTMENT.department" :options="departmentOptions" :placeholder="MOVE_DEPARTMENT.choose" />
      <template v-if="target">
        <SelectField
          id="move-department-team" v-model="ownerTeamId" :label="MOVE_DEPARTMENT.ownerTeam" :options="teamOptions"
          :placeholder="teams.length > 1 ? MOVE_DEPARTMENT.chooseTeam : undefined"
        />
        <GatewayKeyPicker id="move-department-key" v-model="gatewayKeyId" :department-id="target.id" :hint="MOVE_DEPARTMENT.keyHint" />
      </template>
      <div v-if="impact" class="rounded-card border border-border p-3 text-small" role="status" data-test="impact">
        <template v-if="impact.sourcesOnDepartmentPipelines.length">
          <p class="font-medium text-danger">{{ MOVE_DEPARTMENT.blocked }}</p>
          <ul class="ml-5 list-disc"><li v-for="s in impact.sourcesOnDepartmentPipelines" :key="s">{{ s }}</li></ul>
        </template>
        <template v-else-if="impact.shares.length">
          <p class="font-medium">{{ MOVE_DEPARTMENT.shares }}</p>
          <ul class="ml-5 list-disc">
            <li v-for="s in impact.shares" :key="`${s.collectionId}:${s.departmentId}`">{{ MOVE_DEPARTMENT.share(s.collection, s.department) }}</li>
          </ul>
        </template>
        <p v-else>{{ MOVE_DEPARTMENT.noShares }}</p>
      </div>
      <div v-if="refusals.length" ref="refusalBox" role="alert" class="rounded-card border border-danger p-3 text-small text-danger" data-test="refusals">
        <p class="font-medium">{{ MOVE_DEPARTMENT.refused }}</p>
        <ul class="ml-5 list-disc"><li v-for="r in refusals" :key="r">{{ r }}</li></ul>
      </div>
    </form>
    <template #footer>
      <UiButton v-if="impact?.impactToken" variant="primary" type="submit" form="move-department" :loading="busy" data-test="move">{{ MOVE_DEPARTMENT.move }}</UiButton>
      <UiButton v-else variant="primary" type="submit" form="move-department" :loading="busy" :disabled="!target || teamMissing" data-test="check">{{ MOVE_DEPARTMENT.check }}</UiButton>
      <UiButton variant="quiet" @click="emit('update:open', false)">{{ COMMON.cancel }}</UiButton>
    </template>
  </UiDrawer>
</template>

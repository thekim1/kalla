import { computed, ref, watch, type Ref } from 'vue'
import { api } from '@/api'
import type { AssistantTestResult, GeneralKnowledge } from '@/api/types'
import { ASSISTANT } from '@/utils/labels'

export const GENERAL_KNOWLEDGE_LEVELS: GeneralKnowledge[] = ['off', 'supplement', 'fallback', 'open']

/**
 * The test panel's state (admin design §4.9), kept by the assistant page so the question and the answer survive the
 * panel moving between beside the draft, folded under it, and the phone's sheet. The test's general-knowledge level
 * (#726): '' sends none, so the tested version's own level runs; the picker offers only levels up to that one, since a
 * request can only make it stricter.
 */
export function useAssistantTest(assistantId: () => string, testedLevel: Ref<GeneralKnowledge>) {
  const question = ref('')
  const level = ref('')
  const result = ref<AssistantTestResult | null>(null)
  const busy = ref(false)
  const error = ref<unknown>(null)

  const levelOptions = computed(() => [
    { value: '', label: ASSISTANT.testGeneralKnowledgeOwn(ASSISTANT.generalKnowledgeNames[testedLevel.value]!) },
    ...GENERAL_KNOWLEDGE_LEVELS.slice(0, GENERAL_KNOWLEDGE_LEVELS.indexOf(testedLevel.value) + 1)
      .map((g) => ({ value: g, label: ASSISTANT.generalKnowledgeLevels[g]!.label })),
  ])
  watch(levelOptions, (options) => {
    if (!options.some((o) => o.value === level.value)) level.value = ''
  })

  async function ask(): Promise<void> {
    if (!question.value.trim()) return
    busy.value = true
    error.value = null
    try {
      result.value = await api.assistants.test(assistantId(), {
        question: question.value.trim(), ...(level.value ? { generalKnowledge: level.value as GeneralKnowledge } : {}),
      })
    } catch (e) {
      error.value = e
    } finally {
      busy.value = false
    }
  }

  function clear(): void {
    question.value = ''
    level.value = ''
    result.value = null
    error.value = null
  }

  return { question, level, levelOptions, result, busy, error, ask, clear }
}

export type AssistantTest = ReturnType<typeof useAssistantTest>

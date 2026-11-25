import { computed, ref, type Ref } from 'vue'
import { api } from '@/api'
import type { PathRuleJson, Pipeline, Publication } from '@/api/types'
import { FINISHED_STATES } from '@/composables/useJobPoll'
import { usePoll } from '@/composables/usePoll'
import { useAuthStore } from '@/stores/auth'
import { TOPICS } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'

/** A change to one source's `pathRules`: the new rules from the stored ones. */
export type RuleChange = (rules: PathRuleJson[]) => PathRuleJson[]

/**
 * One processing's plan: its publication job, the processing's name, the plan once made, and whether it replaces a draft
 * someone left unpublished (a plan writes the draft, #1075).
 */
export interface PlannedPublication { id: string; processing: string; publication: Publication | null; replacesDraft: boolean }

/** A version's parameters other than the topics, comparable as text (keys sorted). */
function otherParams(params: Record<string, unknown>): string {
  return JSON.stringify(Object.keys(params).filter((k) => k !== 'pathRules').sort().map((k) => [k, params[k]]))
}

/** The stored `pathRules` of a processing's published version (none when it has no published version). */
export function publishedRules(pipeline: Pipeline): PathRuleJson[] {
  const rules = pipeline.versions.find((v) => v.id === pipeline.publishedVersionId)?.definition.params.pathRules
  return Array.isArray(rules) ? (rules as PathRuleJson[]) : []
}

/**
 * Saving a change to the topics (admin design §5.5.2 *Saving*, §6.6; Decisions "Topics editor"): the changes are
 * collected per processing (sources that share one get each change in turn), each processing is planned with its
 * published parameters and the changed `pathRules` (copy-on-write for a department on a platform processing), and once
 * every plan is made one dialog lists each plan's own document count and its sources; **Confirm** publishes them all.
 * A failed plan says so and nothing is confirmed.
 */
export function useTopicPublication(collectionId: Ref<string>, sourceNames: Ref<Map<string, string>>) {
  const auth = useAuthStore()
  const planned = ref<PlannedPublication[]>([])
  const planning = ref(false)
  const error = ref<string | null>(null)
  const confirming = ref(false)
  /** The confirm dialog's title, kept from the plan until the confirm ends. */
  const title = ref('')

  async function plan(changes: Map<string, RuleChange[]>, dialogTitle: string): Promise<void> {
    if (planning.value || changes.size === 0) return
    planning.value = true
    error.value = null
    planned.value = []
    published.clear()
    title.value = dialogTitle
    try {
      const sourceIds = [...changes.keys()]
      const sources = await Promise.all(sourceIds.map((id) => api.sources.get(id)))
      const byPipeline = new Map<string, RuleChange[]>()
      sources.forEach((source, i) => byPipeline.set(source.pipelineId, [...(byPipeline.get(source.pipelineId) ?? []), ...changes.get(sourceIds[i]!)!]))
      for (const [pipelineId, steps] of byPipeline) {
        const pipeline = await api.pipelines.get(pipelineId)
        const published = pipeline.versions.find((v) => v.id === pipeline.publishedVersionId)
        const params: Record<string, unknown> = { ...(published?.definition.params ?? {}) }
        params.pathRules = steps.reduce((rules, step) => step(rules), publishedRules(pipeline))
        // A department's first publication on a platform processing copies it for this knowledge base (§6.6).
        const copyOnWrite = pipeline.departmentId === null && !auth.isPlatformAdmin
        const started = await api.pipelines.plan(pipelineId, {
          parameters: params, version: pipeline.version, ...(copyOnWrite ? { collectionId: collectionId.value } : {}),
        })
        // A draft that changes more than the topics (a source page's unpublished edit); a topic plan's own draft only
        // changes pathRules, and a copy-on-write plan makes a new processing and leaves the platform's draft alone.
        const draft = pipeline.versions.find((v) => v.state === 'draft')
        const replacesDraft = !copyOnWrite && !!draft && otherParams(draft.definition.params) !== otherParams(published?.definition.params ?? {})
        planned.value = [...planned.value, { id: started.publicationId, processing: pipeline.name, publication: null, replacesDraft }]
      }
      poll.start()
    } catch (e) {
      error.value = problemMessage(e)
      planning.value = false
    }
  }

  const poll = usePoll(async (current) => {
    const answers = await Promise.all(planned.value.map((p) => (p.publication && FINISHED_STATES.has(p.publication.state) ? p.publication : api.publications.get(p.id))))
    if (!current()) return true
    planned.value = planned.value.map((p, i) => ({ ...p, publication: answers[i]! }))
    if (!answers.every((a) => FINISHED_STATES.has(a.state))) return false
    planning.value = false
    const failed = planned.value.find((p) => p.publication!.state !== 'succeeded' || !p.publication!.plan)
    if (failed) error.value = TOPICS.planFailed(failed.processing)
    else confirming.value = true
    return true
  })

  /** The plans already published by an earlier Confirm: a retry after a failure skips them (#1075). */
  const published = new Set<string>()

  /** Publishes every plan not yet published; the caller's dialog closes when it resolves, and stays open on a failure. */
  async function confirm(): Promise<void> {
    for (const p of planned.value) {
      if (published.has(p.id)) continue
      await api.publications.confirm(p.id, p.publication!.version)
      published.add(p.id)
    }
  }

  /** The sources a plan processes again: the knowledge base's by name, others counted. */
  function affected(p: PlannedPublication): string {
    const ids = p.publication?.plan?.sources.map((s) => s.sourceId) ?? []
    const names = ids.map((id) => sourceNames.value.get(id)).filter((n): n is string => !!n)
    const others = ids.length - names.length
    return [...names, ...(others > 0 ? [TOPICS.otherSources(others)] : [])].join(', ')
  }

  return { planned, planning: computed(() => planning.value), error, confirming, title, plan, confirm, affected }
}

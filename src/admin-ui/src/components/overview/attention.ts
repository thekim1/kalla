import type { RouteLocationRaw } from 'vue-router'
import type { AttentionItem } from '@/api/types'

// Where each Needs-attention item (admin design §5.5.1, B-59) is dealt with.

const SETTINGS_SECTION: Record<string, string> = {
  'Settings.EvaluationModel': 'evaluation',
  'Settings.EvaluationJudgeModel': 'evaluation',
  'Settings.Chat.CondenseModel': 'chat',
  'Settings.Retrieval.rerankModel': 'retrieval',
  'Settings.Retrieval.gatewayRerankModel': 'retrieval',
}

/** Where the item is dealt with; null when no page of the admin UI is (a worker update). */
export function attentionLink(item: AttentionItem): RouteLocationRaw | null {
  const d = item.details
  switch (item.kind) {
    case 'deletion_waiting':
    case 'run_degraded':
    case 'source_failing':
      return d.runId ? { name: 'run', params: { id: d.runId } } : null
    case 'source_incomplete':
      return item.objectId ? { name: 'source', params: { id: item.objectId } } : null
    case 'evaluation_worse':
    case 'recall_below_median':
      return item.objectId ? { name: 'collection', params: { id: item.objectId }, query: { tab: 'evaluation' } } : null
    case 'personal_data_public':
      return item.objectId ? { name: 'collection', params: { id: item.objectId }, query: { tab: 'documents' } } : null
    case 'assistant_tests_worse':
      return item.objectId ? { name: 'assistant', params: { id: item.objectId }, query: { tab: 'test-questions' } } : null
    case 'answers_below_median':
      return item.objectId ? { name: 'assistant', params: { id: item.objectId }, query: { tab: 'statistics' } } : null
    case 'gateway_key':
      if (!item.objectId) return null
      return item.objectType === 'assistant' ? { name: 'assistant', params: { id: item.objectId } } : { name: 'collection', params: { id: item.objectId } }
    case 'feedback_import_failing':
      return { name: 'feedback' }
    case 'sync_failing':
      return item.objectName === 'models' ? { name: 'platform-models' } : { name: 'organisation' }
    case 'platform_model':
      return { name: 'platform-settings', params: { section: SETTINGS_SECTION[item.objectName] ?? 'evaluation' } }
    default:
      return null
  }
}

import { describe, expect, it } from 'vitest'
import raw from '../../../../contracts/admin/v1/error-codes.json?raw'
import { ERROR_MESSAGES, JOB_MESSAGES, formatMessage, jobMessage } from './messages'

// The exported list of the admin API's error codes (admin design §4.1); CI fails when the UI misses one.
const contract = JSON.parse(raw) as { codes: { code: string; message: string }[] }

describe('error messages', () => {
  it('has a Swedish text for every error code of the API, and no others', () => {
    expect(Object.keys(ERROR_MESSAGES).sort()).toEqual(contract.codes.map((c) => c.code).sort())
  })

  it('uses the same placeholders as the English message', () => {
    const names = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort()
    for (const { code, message } of contract.codes) expect(names(ERROR_MESSAGES[code]!), code).toEqual(names(message))
  })

  it('fills placeholders and drops missing ones', () => {
    expect(formatMessage('Regel ({constraint}).', { constraint: 'CK_X' })).toBe('Regel (CK_X).')
    expect(formatMessage('Regel ({constraint}).')).toBe('Regel ().')
  })
})

// The worker's codes as built (#115, #117; issue #116): every one has a Swedish text.
const WORKER_CODES = [
  'site_profile.unknown', 'site_profile.host_mismatch', 'site_profile.version_unknown', 'site_profile.version_conflict',
  'collection.unknown', 'eval_question.unknown', 'eval_question.conflict', 'eval_question.retired',
  'eval_question.unknown_document', 'run.unknown', 'deletion.not_awaiting', 'source.unknown', 'document.unknown',
  'document.deleted', 'document.no_content', 'source.paused', 'pipeline.unpublished', 'reprocess.fetch_unsupported',
  'command.invalid_payload', 'preview.limit_documents', 'preview.invalid_payload', 'preview.unknown_kind',
  'preview.unsupported', 'preview.collection_unknown', 'preview.source_unknown', 'preview.document_unknown',
  'preview.upload_unknown', 'preview.deadline', 'preflight.none_reachable', 'preflight.deadline', 'pages.within_limit',
  'crawl.limit_pages', 'sitemap.found', 'sitemap.none', 'robots.ok', 'robots.missing', 'cms.sitevision', 'cms.unknown',
]

describe('worker messages', () => {
  it('has a Swedish text for every worker code', () => {
    expect(Object.keys(JOB_MESSAGES).sort()).toEqual([...WORKER_CODES].sort())
  })

  it('falls back to a generic text that names an unknown code', () => {
    expect(jobMessage('robots.ok')).toBe(JOB_MESSAGES['robots.ok'])
    expect(jobMessage('new.code')).toContain('(new.code)')
  })
})

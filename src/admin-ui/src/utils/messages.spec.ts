import { describe, expect, it } from 'vitest'
import raw from '../../../../contracts/admin/v1/error-codes.json?raw'
import { ERROR_MESSAGES, JOB_MESSAGES, JOB_MESSAGE_PREFIXES, formatMessage, jobMessage } from './messages'

// The exported list of the admin API's error codes (admin design §4.1); CI fails when the UI misses one.
const contract = JSON.parse(raw) as { codes: { code: string; message: string }[] }

describe('error messages', () => {
  it('has a UI text for every error code of the API, and no others', () => {
    expect(Object.keys(ERROR_MESSAGES).sort()).toEqual(contract.codes.map((c) => c.code).sort())
  })

  it('uses the same placeholders as the contract message', () => {
    const names = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort()
    for (const { code, message } of contract.codes) expect(names(ERROR_MESSAGES[code]!), code).toEqual(names(message))
  })

  it('fills placeholders and drops missing ones', () => {
    expect(formatMessage('Rule ({constraint}).', { constraint: 'CK_X' })).toBe('Rule (CK_X).')
    expect(formatMessage('Rule ({constraint}).')).toBe('Rule ().')
  })
})

// The worker's codes as built (#115, #117, #128, #130; issues #116, #129): every one has a UI text.
const WORKER_CODES = [
  'site_profile.unknown', 'site_profile.host_mismatch', 'site_profile.version_unknown', 'site_profile.version_conflict',
  'collection.unknown', 'eval_question.unknown', 'eval_question.conflict', 'eval_question.retired',
  'eval_question.unknown_document', 'run.unknown', 'deletion.not_awaiting', 'source.unknown', 'document.unknown',
  'document.deleted', 'document.no_content', 'source.paused', 'pipeline.unpublished', 'reprocess.fetch_unsupported',
  'command.invalid_payload', 'preview.limit_documents', 'preview.invalid_payload', 'preview.unknown_kind',
  'preview.unsupported', 'preview.collection_unknown', 'preview.source_unknown', 'preview.document_unknown',
  'preview.upload_unknown', 'preview.deadline', 'preflight.none_reachable', 'preflight.deadline', 'pages.within_limit',
  'crawl.limit_pages', 'sitemap.found', 'sitemap.none', 'robots.ok', 'robots.missing', 'robots.ignored', 'cms.sitevision', 'cms.unknown',
  // The renderer (RenderReasons, connectors design §8) and the preflight's raw-vs-rendered comparison (Preflight.cs).
  'render.suggested', 'render.compared', 'render.not_configured', 'render.busy', 'render.timeout', 'render.unavailable',
  // The eval class (#128, #130; issue #129).
  'eval.unsupported', 'eval.invalid_payload', 'eval.no_questions', 'eval.invalid_candidate_settings', 'eval.invalid_settings',
  'eval.embedding_residency', 'eval.search_unavailable', 'eval.model_unset', 'eval.model_not_onprem', 'eval.no_onprem_key',
  'eval.unknown_kind',
  // Preflight and fetch outcomes the source drawer shows (Preflight.cs, RawContentWriter.cs).
  'robots.disallow', 'robots.unreachable', 'robots.noindex', 'sitemap.unavailable', 'sitemap.invalid', 'sitemap.too_large',
  'sitemap.truncated', 'http.200', 'http.301', 'http.404', 'http.410', 'http.5xx', 'http.moved_out_of_scope', 'http.content_type',
  'http.body_limit', 'http.unexpected', 'tls.invalid_certificate',
  // Fetch and crawl run-item reasons (HttpFetcher, UrlCanonicalizer, EgressGuard as egress.*, CrawlScope, TrapDetector,
  // HostBudget, CrawlTraversal, SitemapParser).
  'redirect.downgrade', 'redirect.invalid', 'redirect.limit', 'redirect.cookie_required', 'redirect.out_of_scope',
  'http.soft_404', 'http.challenge', 'url.syntax', 'url.scheme',
  'url.userinfo', 'url.host', 'url.port', 'net.dns', 'net.connect', 'net.timeout', 'net.body', 'fetch.abandoned',
  'fetch.invalid_payload', 'http.format_deferred', 'robots.noindex_ignored', 'robots.nofollow_ignored',
  'scope.excluded', 'scope.not_included', 'scope.off_site', 'scope.type', 'trap.endless_path', 'trap.query_variants',
  'trap.calendar', 'crawl.host_blocked', 'crawl.breaker', 'crawl.retry_after', 'crawl.lastmod_skip',
  'crawl.removal_check', 'sitemap.depth', 'sitemap.dtd', 'identity.folded', 'egress.private_address',
  'egress.embedded_address', 'egress.scheme', 'egress.port', 'egress.userinfo', 'egress.blocked_host',
  'egress.redirect_downgrade',
  // Crawl completeness reasons (TraversalCompleteness.cs).
  'crawl.incremental', 'crawl.host_paused', 'crawl.seeds_failed', 'crawl.seeds_unreachable', 'crawl.claims_pending', 'crawl.cancelled',
  // Preview stage outcomes the preview result shows (PreviewSegment.cs).
  'parse.empty', 'raw.missing',
  // Job and run-item reasons of processing, embedding, the gateway, evaluation, the parser, personal data and uploads.
  'clamav.limit', 'clamav.timeout', 'clamav.unavailable', 'change.noise', 'rule.unaffected', 'version.unaffected',
  'version.swapped_back', 'execution.superseded', 'pipeline.republished', 'pipeline.refused', 'plan.version_unknown',
  'document.concurrent_insert', 'job.exception', 'node.fatal', 'node.transient', 'command.unknown_kind',
  'run.cancelled', 'sync.invalid_payload', 'sync.unsupported', 'sync.run_in_progress', 'parse.invalid_payload',
  'embed.invalid_payload', 'deletion.rejected', 'deletion.superseded', 'sample.too_small', 'embed.bad_vector',
  'embed.canary_mismatch', 'embed.input_missing', 'embed.input_rejected', 'embed.profile_unknown', 'gateway.timeout',
  'gateway.unreachable', 'gateway.unavailable', 'gateway.rate_limited', 'gateway.budget_exhausted',
  'gateway.residency_above_ceiling', 'gateway.forbidden', 'eval.embedding_profile_unknown',
  'eval.embedding_unavailable', 'eval.model_not_at_ceiling', 'eval.model_unknown', 'eval.no_key',
  'eval.request_rejected', 'eval.stopped', 'parser.unavailable', 'parser.not_configured', 'parser.encrypted',
  'parser.too_large', 'parser.too_many_pages', 'parser.format_deferred', 'parser.unsupported_format',
  'parser.limit_exceeded', 'parser.timeout', 'parser.worker_crashed', 'parser.resource_limit', 'parser.ocr_failed',
  'parser.conversion_failed', 'parser.ner_not_ready', 'pii.flagged', 'pii.ner_pending', 'upload.too_large',
  'upload.manifest_missing', 'upload.blob_missing', 'upload.blob_damaged', 'upload.hash_mismatch', 'upload.deleted',
  'upload.duplicate', 'upload.unknown_slot', 'upload.slot_in_use', 'upload.format_deferred', 'upload.macro_enabled',
  'upload.archive', 'upload.type_unknown', 'upload.type_mismatch',
]

describe('worker messages', () => {
  it('has a UI text for every worker code', () => {
    expect(Object.keys(JOB_MESSAGES).sort()).toEqual([...WORKER_CODES].sort())
  })

  it('falls back to a generic text that names an unknown code', () => {
    expect(jobMessage('robots.ok')).toBe(JOB_MESSAGES['robots.ok'])
    expect(jobMessage('new.code')).toContain('(new.code)')
  })

  it('gives a code built at run time the text of its longest prefix, naming the code', () => {
    expect(jobMessage('gateway.http_502')).toBe(`${JOB_MESSAGE_PREFIXES['gateway.http_']} (gateway.http_502)`)
    expect(jobMessage('gateway.model_not_allowed')).toBe(`${JOB_MESSAGE_PREFIXES['gateway.']} (gateway.model_not_allowed)`)
    expect(jobMessage('parser.ner_busy')).toBe(`${JOB_MESSAGE_PREFIXES['parser.ner_']} (parser.ner_busy)`)
    expect(jobMessage('gateway.timeout')).toBe(JOB_MESSAGES['gateway.timeout'])
  })

  it('tells why a page can come out empty', () => {
    expect(jobMessage('parse.empty')).toContain('JavaScript')
  })
})

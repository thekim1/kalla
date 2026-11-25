/** The admin API's types (Kalla.AdminApi). JSON is camelCase; dates are ISO-8601 with offset. */

/** ISO-8601 timestamp with offset. */
export type IsoDateTime = string

/** Admin roles (identity design §1.3), as `Kalla.Domain.Admin.RagRoles`. */
export const ROLES = [
  'rag-platform-admin',
  'rag-department-admin',
  'rag-pipeline-editor',
  'rag-reviewer',
  'rag-skill-editor',
  'rag-editor',
  'rag-viewer',
] as const
export type Role = (typeof ROLES)[number]

/** RFC 9457 problem details; admin faults add `code` and `params`, validation faults `errorCodes` (admin design §4.2). */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
  errors?: Record<string, string[]>
  code?: string
  params?: Record<string, string>
  errorCodes?: Record<string, { code: string; params?: Record<string, string> }[]>
  [extension: string]: unknown
}

// ---------- BFF ----------
export type DepartmentSource = 'claim' | 'group'

export interface BffUser {
  isAuthenticated: boolean
  /** ASP.NET Core environment name of the admin API, for example Development or Production. */
  environment?: string
  /** The gateway's organisation name (its context's `organisation_name`), shown in the header and on the sign-in page; null when it has none. */
  organisationName?: string | null
  /** The organisation's logo from the gateway's admin console, served by kalla-admin (`/bff/branding/logo?v=…`); null shows the built-in mark. */
  logoUrl?: string | null
  name: string | null
  email: string | null
  roles: string[]
  departmentCodes: string[]
  /** Where each department came from: the department claim, or a member group (identity design §1.5). */
  departmentSources: { code: string; source: DepartmentSource }[]
  teamCodes?: string[]
  sessionExpiresAt: IsoDateTime | null
  /** The session's absolute end (Admin:MaxSessionHours after sign-in); extending never goes past it. */
  sessionLimitAt?: IsoDateTime | null
  /** False while kalla-admin has no usable identity provider (identity design §19, "Reload"). */
  signIn: { configured: boolean }
  /** Whether setup mode is open: the wizard at /setup takes the setup code (identity design §19, "Setup state"). */
  setup: { open: boolean }
  /** Whether the deployment runs Open WebUI (kalla-admin's `OpenWebUi:Enabled`, its `openwebui` profile); without it the UI leaves Open WebUI out. */
  openWebUi: { enabled: boolean }
}

// ---------- Setup mode (identity design §19; admin design §5.11) ----------
/** Where an effective sign-in value comes from (`SignInValueSources`). */
export type SignInValueSource = 'deployment' | 'wizard' | 'default'

export interface SignInValue<T> {
  value: T
  source: SignInValueSource
}

/** The client secret as the API shows it: never the value (`SignInSecretStatus`). */
export interface SignInSecretStatus {
  set: boolean
  source: SignInValueSource
  setAt: IsoDateTime | null
  /** `yyyy-mm-dd`; only while the wizard's secret applies. */
  expiresOn: string | null
  unreadable: boolean
}

/** kalla-admin's sign-in values, each with its source (`SignInSettingsDto`). */
export interface SignInSettings {
  authority: SignInValue<string | null>
  clientId: SignInValue<string>
  clientSecret: SignInSecretStatus
  roleClaim: SignInValue<string>
  departmentClaim: SignInValue<string>
  teamClaim: SignInValue<string>
  groupClaim: SignInValue<string>
  extraScopes: SignInValue<string[]>
  tenantId: SignInValue<string | null>
  refuseGuests: SignInValue<boolean>
  version: string
}

/** The wizard's values (`OidcWizardValues`): null is "not set in the wizard" (the deployment's value or the default applies). */
export interface OidcWizardValues {
  authority: string | null
  clientId: string | null
  roleClaim: string | null
  departmentClaim: string | null
  teamClaim: string | null
  groupClaim: string | null
  extraScopes: string[] | null
  tenantId: string | null
  refuseGuests: boolean | null
  /** `yyyy-mm-dd`. */
  clientSecretExpiresOn: string | null
}

/** `POST /bff/setup/test`. `clientSecret`: null uses the stored one, "" tests without one, a string tests with it. */
export interface SetupValuesRequest extends OidcWizardValues {
  clientSecret: string | null
}

/** `POST /bff/setup/complete`: the tested values exactly, without the secret (the test row holds it). */
export interface SetupCompleteRequest extends OidcWizardValues {
  testId: string
}

/** `POST /bff/setup/session`. */
export interface SetupSessionStarted {
  expiresAt: IsoDateTime
  setupEndsAt: IsoDateTime
}

/** The claim names and checks a test read (`SetupClaimsRead`). */
export interface SetupClaimsRead {
  role: string
  group: string
  department: string
  team: string
  tenantId: string | null
  refuseGuests: boolean
}

/** The tested person (`SetupTestResult`); lists hold at most 100 entries, `groupCount` is the whole count. */
export interface SetupTestResult {
  subject: string
  name: string | null
  roles: string[]
  groups: string[]
  groupCount: number
  departmentCodes: string[]
  teamCodes: string[]
  isPlatformAdmin: boolean
  claimsRead: SetupClaimsRead
}

export type SetupTestStatus = 'pending' | 'passed' | 'failed' | 'incomplete'

/** Where a test's client secret came from (`SetupSecretSources`). */
export type SetupSecretSource = 'deployment' | 'typed' | 'stored' | 'removed' | 'none'

/** A test as the wizard shows it (`SetupTestDto`). */
export interface SetupTest {
  testId: string
  target: string
  status: SetupTestStatus
  reason: string | null
  startedAt: IsoDateTime
  expiresAt: IsoDateTime
  values: OidcWizardValues | null
  clientSecret: SetupSecretSource | null
  result: SetupTestResult | null
}

/**
 * What the gateway's context says of its own sign-in (`SetupGatewaySignIn`, identity design §19): `suggestedAuthority` is the gateway's
 * provider address, a suggestion for kalla-admin's authority only (null while the gateway has none); `adminUrl` is the gateway admin's
 * address for the link to its own setup.
 */
export interface SetupGatewaySignIn {
  suggestedAuthority: string | null
  adminUrl: string | null
}

/** `GET /bff/setup/state`. `gateway` is null while the gateway lacks the feature `oidc-authority`. */
export interface SetupState {
  kind: 'first_run' | 'reopen'
  endsAt: IsoDateTime
  sessionExpiresAt: IsoDateTime
  signIn: SignInSettings
  redirectUris: { signIn: string; signOutCallback: string }
  deployment: { gatewayBaseUrl: string | null; apiPublicUrl: string | null; platformAdminGroups: string[] }
  gateway: SetupGatewaySignIn | null
  latestTest: SetupTest | null
  openWebUi: SetupOpenWebUiState
}

/** Which client a setup test or a client secret belongs to (`SetupTargets`). */
export type SetupTarget = 'rag-admin' | 'open-webui'

/** Open WebUI's sign-in values, each with its source (`OpenWebUiSignInDto`); `providerUrl`'s default is null while no authority is known. */
export interface OpenWebUiSignIn {
  providerUrl: SignInValue<string | null>
  clientId: SignInValue<string | null>
  providerName: SignInValue<string | null>
  clientSecret: SignInSecretStatus
  configured: boolean
}

/** Open WebUI's values as `POST test` and `POST complete` take them for `target: "open-webui"` (`OpenWebUiOidcValues`). */
export interface OpenWebUiWizardValues {
  providerUrl: string | null
  clientId: string | null
  providerName: string | null
  /** `yyyy-mm-dd`. */
  clientSecretExpiresOn: string | null
}

/**
 * What an Open WebUI check found (`OpenWebUiTestResult`): the discovery document (`ok`; a failed one refuses the check instead), and
 * whether the token endpoint accepted the client (true), refused it (false) or could not tell (null, with `clientReason`).
 */
export interface OpenWebUiTestResult {
  discovery: string
  clientAccepted: boolean | null
  clientReason: string | null
}

/** An Open WebUI check (`OpenWebUiTestDto`), answered at once: it has no browser leg, so it is never pending. */
export interface OpenWebUiTest {
  testId: string
  target: string
  status: 'passed' | 'failed'
  reason: string | null
  startedAt: IsoDateTime
  values: OpenWebUiWizardValues | null
  clientSecret: SetupSecretSource | null
  result: OpenWebUiTestResult | null
}

/** Open WebUI in `GET /bff/setup/state` (`SetupOpenWebUiState`): `redirectUri` is null while kalla-admin has no `OpenWebUi:PublicUrl`. */
export interface SetupOpenWebUiState {
  enabled: boolean
  signIn: OpenWebUiSignIn
  redirectUri: string | null
  latestTest: OpenWebUiTest | null
}

/** `POST /bff/setup/test` for Open WebUI: only its own members (a member of the other target is a 400). */
export interface OpenWebUiTestRequest extends OpenWebUiWizardValues {
  target: 'open-webui'
  clientSecret: string | null
}

/** `POST /bff/setup/complete` for Open WebUI: the checked values exactly, without the secret. It does not close setup. */
export interface OpenWebUiCompleteRequest extends OpenWebUiWizardValues {
  target: 'open-webui'
  testId: string
}

/** The answer of `POST /bff/setup/complete` for Open WebUI (`SetupOpenWebUiSavedDto`). */
export interface OpenWebUiSaved {
  openWebUi: { enabled: boolean; signIn: OpenWebUiSignIn; redirectUri: string | null }
  /** Whether the check confirmed the client's credentials; false: a wrong secret shows only when a user signs in. */
  clientVerified: boolean
}

/** `POST /bff/setup/test`: the provider address the wizard opens. */
export interface SetupTestStarted {
  testId: string
  authorizeUrl: string
  expiresAt: IsoDateTime
}

// ---------- Organisation ----------
export interface Team {
  id: string
  name: string
  code: string | null
  isActive: boolean
}

/** A move to another department (#811, admin design §3.2): the impact's token is required by the move. */
export interface MoveRequest {
  departmentId: string
  ownerTeamId: string | null
  gatewayKeyId: string | null
  version: string
  impactToken?: string
}

/** What a move does: the shares it adds to keep links working, and the sources that block it (then no token). */
export interface MoveImpact {
  shares: { collectionId: string; collection: string; departmentId: string; department: string }[]
  sourcesOnDepartmentPipelines: string[]
  /** The owner team it gets: the one chosen, else the new department's only active team. */
  ownerTeamId: string | null
  /** Links that do not work today (the department cannot reach the knowledge base): the move leaves them broken. */
  brokenLinks: { assistantId: string; assistant: string; department: string; collectionId: string; collection: string }[]
  /** Client keys of other departments scoped to the object: they keep working; their scope needs a look. */
  scopedKeys: { keyId: string; key: string; department: string }[]
  impactToken: string | null
}

export interface Department {
  id: string
  name: string
  costCenterCode: string
  isActive: boolean
  syncedAt: IsoDateTime
  teams: Team[]
  /** The gateway's member groups of the department (identity §2.1). */
  memberGroups?: string[]
  /** How many of its knowledge bases are shared at each level, and with how many departments (admin design §3.2). */
  sharing?: { department: number; selected: number; municipality: number; sharedWithDepartments: number }
}

export interface OrgSyncResult {
  departments: number
  teams: number
}

/** The gateway's last context as the organisation sync holds it (`GatewayContextInfo`, admin design §4.10). */
export interface GatewayContextInfo {
  version: string | null
  adminUrl: string | null
  features: string[]
}

export interface OrgSyncStatus {
  enabled: boolean
  lastRunAt: IsoDateTime | null
  lastSuccessAt: IsoDateTime | null
  consecutiveFailures: number
  lastError: string | null
  lastResult: OrgSyncResult | null
  /** The last context's version, admin URL and features; null before the first read (#619). */
  gateway: GatewayContextInfo | null
  /** Why departments and teams were left as they are; null when they were taken. */
  degraded: string | null
  /** The gateway links' address: the context's `admin_url`, else `Admin:GatewayAdminUrl` (admin design §2.3). */
  adminUrl: string | null
  /** The Integration API read-back's state (B-45, #642): why a key's cost and budget are missing. */
  integration?: IntegrationReadback | null
}

/** Whether the gateway's context names its Integration API address (`integration_api_url`), and the last read-back's refusal (`401`, `403` scope missing, `429`, or a failure). */
export interface IntegrationReadback {
  configured: boolean
  lastError: { reason: string; status: number | null; at: IsoDateTime } | null
}

// ---------- Collections, sources, documents (admin design §4.5) ----------
export type Level = 'public' | 'internal' | 'sensitive'
export type SourceType = 'url-list' | 'crawl' | 'upload' | 'sharepoint'

export interface Page<T> {
  items: T[]
  total: number
}

export interface KeysetPage<T, K = string> {
  items: T[]
  next: K | null
}

/** Who may link a knowledge base besides its own department (admin design §3.2). */
export type SharingLevel = 'department' | 'selected' | 'municipality'

interface CollectionBase {
  id: string
  slug: string
  name: string
  departmentId: string
  classification: Level
  publishedAt: IsoDateTime | null
  sharedWith: SharingLevel
  contactName: string | null
  contactEmail: string | null
}

/**
 * `GET /api/collections?view=` rows. For another department's knowledge base (`shared`) this is all a department it is
 * shared with sees: name, owner, level, contact, document count and topics (admin design §4.5).
 */
export interface CollectionSummary extends CollectionBase {
  departmentName: string
  shared: boolean
  documentCount: number
  topics: string[]
  /** The latest run of any of its sources (the list's Last fetch, admin design §5.5.2); null before the first and on a shared row. */
  lastRun: { id: string; sourceId: string; state: string; startedAt: IsoDateTime; finishedAt: IsoDateTime | null } | null
  /** The latest finished evaluation's recall@10 (the list's Quality); null before the first and on a shared row. */
  latestEvaluation: { recallAtK: number; finishedAt: IsoDateTime } | null
}

export type CollectionView = 'mine' | 'shared' | 'all'

/** An assistant that links the collection, through its published version, its draft, or both; archived ones too. */
export interface CollectionUser {
  id: string
  name: string
  classification: Level
  state: 'active' | 'archived' | string
  published: boolean
  draft: boolean
  departmentId: string
  departmentName: string
  /** The link's topics; empty = the whole knowledge base. */
  topics: string[]
}

/** The owner's detail. */
export interface Collection extends CollectionBase {
  shared: false
  ownerTeamId: string | null
  /** The assigned gateway key its indexing and evaluation run as (V39); null = the worker's credential key. */
  gatewayKeyId?: string | null
  embeddingProfileId: string
  retrievalSettings: Record<string, unknown>
  /** Only with `selected`. */
  sharedWithDepartmentIds: string[]
  /** Live documents (they can be found), the same count as the list row's. */
  documentCount: number
  usedBy: CollectionUser[]
  updatedAt: IsoDateTime
  updatedBy: string | null
  version: string
}

/** `GET /api/collections/{id}`: the owner's detail, or the shared summary for a department it is shared with. */
export type CollectionRead = Collection | (CollectionSummary & { shared: true })

/** `PUT /api/collections/{id}`: the collection's own fields, the level included (#446). */
export interface CollectionUpdate {
  name: string
  ownerTeamId: string | null
  classification: Level
  contactName: string | null
  contactEmail: string | null
  /** V39 (B-36); null = the worker's credential key. */
  gatewayKeyId?: string | null
  version: string
}

/** `PUT /api/assistants/{id}`: the assistant's own fields, the level included (#446); V36's confirmation (#445). */
export interface AssistantUpdate {
  name: string
  ownerTeamId: string | null
  classification: Level
  description: string | null
  contactName: string | null
  contactEmail: string | null
  /** V39 (B-36); null = the platform's credential key. */
  gatewayKeyId?: string | null
  version: string
  confirmGeneralKnowledge?: boolean
}

export interface CollectionCreate {
  name: string
  departmentId: string
  classification: Level
  contactName: string | null
  contactEmail: string | null
}

/**
 * `GET /api/collections/{id}/topics`: `rules` only for the owning department. `pending`: a published rule sets the tag while
 * its source is processed again and no live chunk carries it yet; `documents` is then null (admin design §4.5).
 */
export interface Topic {
  tag: string
  documents: number | null
  rules: { sourceId: string; sourceName: string; on: string; match: string }[] | null
  pending: boolean
}

/** One section to pick on the Topics tab (`GET /api/collections/{id}/topic-suggestions`, admin design §4.5). */
export interface TopicSection {
  /** `/bygga-bo-och-miljo/` on a website, `HR/` for an upload's folder. */
  path: string
  documents: number
  title: string | null
  /** null when the segment has no letter or digit to make a tag of. */
  suggestedTag: string | null
  /** The topics whose path rules already cover the section. */
  coveredBy: string[]
}

/** One rule of processing §7's `pathRules`. */
export interface PathRuleJson {
  match: string
  on: 'path' | 'path-regex' | 'host' | 'title-regex' | 'mediaType'
  /** Optional in the schema: a rule may set only `documentType` or `date` (#1076). */
  tags?: string[]
  documentType?: string
  date?: string
}

/** `POST /api/collections/{id}/topic-rules/preview`: what one rule matches among a source's live documents (≤ 10 listed). */
export interface TopicRulePreview {
  matched: number
  total: number
  /** The matching's time budget ended after `checked` documents (#1076): `matched` counts those only. */
  checked: number
  partial: boolean
  sample: { documentId: string; title: string | null; url: string | null; displayName: string | null }[]
}

/** One source's sections; `under` is the section whose next level this is, null for the first level. */
export interface SourceTopicSuggestions {
  sourceId: string
  sourceName: string
  kind: 'website' | 'upload'
  under: string | null
  sections: TopicSection[]
}

/** One row of the Map (admin design §5.5.6): department → knowledge base → assistant → audience. */
export interface MapRow {
  collection: {
    id: string; name: string; departmentId: string; departmentName: string; shared: boolean; sharedWith: SharingLevel; classification: Level
    published: boolean; contactName: string | null; contactEmail: string | null
    /** An own knowledge base's sources, by name (the drawing's first column); none for a shared one. */
    sources: { id: string; name: string; type: string }[]
  }
  assistant: {
    id: string; name: string; state: string; publishedVersion: number | null; draftOnly: boolean; topics: string[]; contactName: string | null
    contactEmail: string | null
  } | null
  audience: { clientId: string; name: string; kind: string } | null
}

export interface DepartmentMap {
  departmentId: string
  departmentName: string
  rows: MapRow[]
}

export interface SourceSummary {
  id: string
  collectionId: string
  type: SourceType
  name: string
  classification: Level
  state: string
}

/** `GET /api/collections/{id}/sources`: a source with its document count and latest run. */
export interface SourceListItem extends SourceSummary {
  documents: number
  lastRun: { id: string; state: string; startedAt: IsoDateTime; finishedAt: IsoDateTime | null } | null
}

export interface DocumentSummary {
  id: string
  sourceId: string
  title: string | null
  url: string | null
  displayName: string | null
  mediaType: string | null
  parseOutcome: string | null
  piiFlagged: boolean
  piiCleared: boolean
  /** A flagged chunk set is held for personal data now, also after an earlier clearance (processing §9.5). */
  piiHeld: boolean
  held: boolean
  live: boolean
  sourceModifiedAt: IsoDateTime | null
  fetchedAt: IsoDateTime | null
  lastSeenAt: IsoDateTime
}

export interface Source extends SourceSummary {
  settings: Record<string, unknown>
  /** Its own cron; null: the platform default for the type (engine design §9.6). */
  schedule: string | null
  /** The cron it runs on (its own or the default); null for uploads. */
  scheduleInForce: string | null
  /** When the next scheduled fetch starts, the spread included; not after now: due; null when paused or unscheduled. */
  nextRunAt: IsoDateTime | null
  pipelineId: string
  containsThirdPartyText: boolean
  updatedAt: IsoDateTime
  updatedBy: string | null
  version: string
}

/**
 * `POST /api/sources/{id}/impact`: what a settings change does (`refetch`: every page is fetched again, a `fetchMode`,
 * `readySelector` or `scrollToLoad` change); `impactToken` when a scope or identity change needs confirming (V25).
 */
export interface SourceImpact {
  effects: ('identity' | 'scope' | 'behaviour' | 'cosmetic' | 'refetch' | string)[]
  liveDocuments: number
  usedBy: CollectionUser[]
  impactToken: string | null
}

export interface SourceUpdate {
  name: string
  classification: Level
  settings: Record<string, unknown>
  schedule: string | null
  impactToken?: string
  version: string
}

/** `GET /api/runs/{id}/deletions`: a document the run proposes to delete (connectors §13). */
/** The configured deletion guard (`Settings.DeletionGuard*`, connectors §13.2). */
export interface DeletionGuardLimits {
  percent: number
  minCount: number
  maxCount: number
}

/** `GET /api/runs/{id}/deletions`: a page of proposals with the guard's limits. */
export interface DeletionProposalPage extends KeysetPage<DeletionProposal> {
  guard: DeletionGuardLimits
}

export interface DeletionProposal {
  documentId: string
  title: string | null
  url: string | null
  reason: string
  exempt: boolean
  decision: string | null
  lastSeenAt: IsoDateTime
}

/** `GET /api/sources/{id}/held`: a document kept by a rejected deletion. */
export interface HeldDocument {
  id: string
  title: string | null
  url: string | null
  displayName: string | null
  heldAt: IsoDateTime
  heldBy: string | null
  lastSeenAt: IsoDateTime
}

/** `GET /api/sources/{id}/files`: an upload slot. */
export interface UploadFile {
  id: string
  fileName: string
  bytes: number
  state: 'received' | 'clean' | 'unscanned' | 'malware' | 'refused' | 'indexed' | 'deleted' | string
  reason: string | null
  documentId: string | null
  uploadedBy: string | null
  uploadedAt: IsoDateTime
  replacedAt: IsoDateTime | null
  deletedAt: IsoDateTime | null
  /** When its current bytes passed a malware scan; null when they were not scanned (the scan is off, #535). */
  scannedAt?: IsoDateTime | null
}

/** The `state` filter of `GET /api/sources/{id}/files` (admin design §5.5.7): processing is received, clean and unscanned, failed is refused and malware. */
export type FileStateFilter = 'processing' | 'indexed' | 'failed'

/** The `sort` of `GET /api/sources/{id}/files`: path order (a folder's files together) or newest first. */
export type FileSort = 'name' | 'uploaded'

/** `GET /api/sources/{id}/files/summary`: the source's files that are not deleted. */
export interface UploadSummary {
  total: number
  processing: number
  indexed: number
  failed: number
  bytes: number
  lastUploadAt: IsoDateTime | null
  /** Files not scanned for malware: `unscanned`, or `indexed` without `scannedAt` (admin design §5.5.7). */
  notScanned: number
}

/** `GET /api/sources/{id}/quarantine`: metadata only (connectors §11). */
export interface QuarantinedFile {
  id: string
  origin: 'upload' | 'crawl' | string
  name: string | null
  url: string | null
  signature: string
  runId: string | null
  detectedAt: IsoDateTime
  purgeAfter: IsoDateTime
}

export interface UploadBatch {
  batchId: string
  maxFiles: number
  maxBytes: number
}

export interface UploadedFile {
  n: number
  uploadId: string
  operation: 'add' | 'replace' | string
  fileName: string
  bytes: number
  sha256: string
}

export interface SourceCreate {
  type: SourceType
  name: string
  classification: Level
  settings: Record<string, unknown>
  schedule?: string | null
}

export interface SyncStarted {
  jobId: string | null
  runId: string | null
  alreadyRunning: boolean
  kind: string | null
}

/** A job the UI polls (admin design §4.7): `state` queued, running, succeeded, failed, dead or cancelled. */
export interface Job<TResult = unknown> {
  id: string
  class: string
  kind: string | null
  state: string
  enqueuedAt: IsoDateTime
  completedAt: IsoDateTime | null
  runId: string | null
  result: TResult | null
  lastError: string | null
}

export type CheckState = 'pass' | 'warn' | 'fail'

/** A preflight entry's facts; a `render` entry has the page's main-text words as fetched and as rendered (connectors design §8.1). */
export interface PreflightFacts {
  rawWords?: number
  renderedWords?: number
  [fact: string]: unknown
}

/** contracts/jobs/v1/preview.preflight.result.schema.json */
export interface PreflightResult {
  state: CheckState
  checks: { check: string; state: CheckState; code: string; target?: string | null; facts?: PreflightFacts | null }[]
  estimatedPages?: number | null
  cms?: string | null
}

/** `GET /api/pipelines?collectionId&sourceType`: a pipeline a new source can use; `isDefault` is the one it gets without a choice. */
export interface PipelineSummary {
  id: string
  name: string
  templateId: string
  departmentId: string | null
  publishedVersionId: string | null
  isDefault: boolean
}

export interface PipelineVersion {
  id: string
  number: number
  state: 'draft' | 'published' | 'superseded' | string
  definition: { schema?: number; template: string; templateVersion: number; params: Record<string, unknown> }
  publishedAt: IsoDateTime | null
}

/** `GET /api/pipelines/{id}`: the pipeline with its versions, newest first. */
export interface Pipeline {
  id: string
  name: string
  templateId: string
  departmentId: string | null
  publishedVersionId: string | null
  versions: PipelineVersion[]
  version: string
}

/** contracts/jobs/v1/preview.publish-plan.result.schema.json */
export interface PublishPlan {
  pipelineVersionId: string
  documents: number
  sources: { sourceId: string; documents: number }[]
  fromStage?: string
  parserPages?: number
  embeddingTokens?: number
  estimatedMinutes?: number
  estimatedCostUsd?: number
}

/** `GET /api/publications/{id}`: the plan job, its draft and the pipeline version the confirm sends. */
export interface Publication {
  id: string
  state: string
  plan: PublishPlan | null
  lastError: string | null
  pipelineId: string
  pipelineVersionId: string
  draftNumber: number | null
  draftState: string | null
  version: string
  published: boolean
  superseded: boolean
}

export interface PublicationStarted {
  publicationId: string
  pipelineId: string
  draftVersionId: string
  draftNumber: number
  copiedOnWrite: boolean
}

export interface EvaluationRun {
  id: string
  kind: string
  variant: string
  questionCount: number | null
  k: number
  found: number | null
  recallAtK: number | null
  mrr: number | null
  wins: number | null
  losses: number | null
  comparedRunId: string | null
  /** The compared run's recall@k; null without one (the Quality dot's "worse", Home's rule; #1049). */
  comparedRecallAtK?: number | null
  candidate: boolean
  /** The worker's coverage-threshold proposal (processing design §13.6), as stored; null without human questions. */
  thresholdProposal: CoverageProposal | null
  startedAt: IsoDateTime
  finishedAt: IsoDateTime | null
  /** Questions left out because an expected document was deleted (processing design §13.3); null once the run's job is past retention. */
  documentGone: number | null
}

/** A proposed threshold and how many human questions it misclassifies. */
export interface ThresholdProposal {
  value: number
  misclassified: number
}

/** `EvaluationRuns.ThresholdProposal` (admin design §7.1): the human layer's not-found counts and the proposed thresholds. */
export interface CoverageProposal {
  answerable: number
  unanswerable: number
  falseNotFound?: number
  unanswerableNotFound?: number
  coverageMaxDistance?: ThresholdProposal | null
  coverageMinKeywordScore?: ThresholdProposal | null
}

/** `GET /api/evaluation-runs/{id}/results`: one question's rank in the run and in the compared run. */
export interface EvaluationResult {
  questionId: string
  question: string
  layer: 'generated' | 'human' | string
  rank: number | null
  previousRank: number | null
  /** Null for an unanswerable question (no expected documents). */
  hit: boolean | null
  coverage: 'ok' | 'low' | string | null
  bestDistance: number | null
  bestKeywordScore: number | null
}

export interface EvaluationQuestion {
  id: string
  layer: 'generated' | 'human' | string
  state: 'active' | 'stale' | 'retired' | string
  question: string
  expectedAnswer: string | null
  expectedDocumentIds: string[]
  origin: string | null
  createdBy: string | null
  retiredAt: IsoDateTime | null
}

export interface EvaluationQuestionInput {
  question: string
  expectedAnswer: string | null
  expectedDocumentIds: string[]
}

/** `GET /api/collections/{id}/evaluation/generate`: what generating test questions would do now. */
export interface GenerationForecast {
  kind: 'generate-set' | 'top-up-set' | string
  model: string | null
  judgeModel: string | null
  liveDocuments: number
  eligibleChunks: number
  activeQuestions: number
  target: number
  needed: number
  modelCalls: number
  estimatedTokens: number
  estimatedCostUsd: number | null
}

/** `GET /api/documents/{id}`, the fields the admin UI reads. */
export interface DocumentDetail {
  id: string
  sourceId: string
  title: string | null
  url: string | null
  displayName: string | null
  mediaType: string | null
  liveChunkSetId: string | null
  chunkSets: { id: string; state: string; chunkCount: number }[]
}

/** `GET /api/documents/{id}/chunks`: the live chunk set's chunks (at most 1000; `truncated` when there are more). */
export interface DocumentChunks {
  documentId: string
  chunkSetId: string | null
  chunks: DocumentChunk[]
  truncated: boolean
}

export interface DocumentChunk {
  id: string
  ordinal: number
  headingPath: string | null
  text: string
  tokenCount: number
  page: number | null
  pageEnd: number | null
  contentKind: string
}

export interface EvaluationOverview {
  hasSet: boolean
  questions: { layer: string; state: string; count: number }[]
  latestRuns: EvaluationRun[]
}

export interface TemplateDetail {
  id: string
  version: number
  sourceType: string
  deprecated: boolean
  stages: string[]
  paramsSchema: { properties?: Record<string, Record<string, unknown>> }
}

export type PreviewStage = 'fetch' | 'scan' | 'detect' | 'parse' | 'clean' | 'chunk' | 'personal-data' | 'embed' | 'index'

/** contracts/jobs/v1/preview.stage-preview.result.schema.json */
export interface PreviewStageResult {
  stage: PreviewStage
  state: 'succeeded' | 'failed' | 'skipped'
  durationMs?: number
  cacheHit?: boolean
  facts?: Record<string, unknown> | null
  estimatedCostUsd?: number | null
  costUsd?: number | null
  error?: string | null
  output?: string | null
  truncated?: boolean
}

export interface PreviewSiteProfile {
  host: string
  origin: 'manual' | 'preset'
  siteProfileVersionId?: string | null
  presetId?: string | null
  before: string
  after: string
}

export interface PreviewDocument {
  documentId?: string | null
  url?: string | null
  uploadId?: string | null
  stages: PreviewStageResult[]
  siteProfile?: PreviewSiteProfile | null
}

export interface StagePreviewResult {
  documents: PreviewDocument[]
  expiresAt: IsoDateTime
}

/** A preview output blob: the stage's value as the worker shows it, cut at 2 MB with `truncated`. */
export interface PreviewOutput<T> {
  stage: string
  truncated: boolean
  value: T | null
}

export interface PreviewBlock {
  kind: string
  text: string
  level?: number | null
  layer?: string
}

/** The parse and clean outputs (and the site profile's before/after): the extracted blocks and facts. */
export interface PreviewParsed {
  title?: string | null
  blocks: PreviewBlock[]
  breadcrumb?: string[]
  visibleDate?: string | null
  documentDate?: string | null
}

export interface PreviewChunks {
  chunks: { ordinal: number; prefix: string; headingPath: string; text: string; tokenCount: number; page?: number | null }[]
}

export interface PreviewPii {
  counts: Record<string, number | string | null>
  typesFired: string[]
  state: string
  /** With `piiSpans` (the highlights): each match by chunk ordinal, type and offsets in the chunk's text; never the value. */
  spans?: { ordinal: number; type: string; start: number; length: number }[] | null
}

export interface RunSummary {
  id: string
  kind: string
  trigger: string
  state: string
  phase: string | null
  added: number
  updated: number
  unchanged: number
  deleted: number
  failed: number
  proposedDeletions: number
  costUsd: number
  startedAt: IsoDateTime
  finishedAt: IsoDateTime | null
  /** Only while the run is `running` (#803); the counters above are then derived from its items so far. */
  progress?: RunProgress | null
}

/**
 * How far a running run has got (AdminApi `RunProgress`, #803): the documents handled so far and, for a crawl, its
 * frontier (null for other source types).
 */
export interface RunProgress {
  processed: number
  admitted: number | null
  maxPages: number | null
  waiting: number | null
  seeds: number | null
  seedsFailed: number | null
  limitHit: boolean | null
}

/**
 * A scope host crawled with a non-default politeness profile (worker `PolitenessReportEntry`, connectors design §6.6):
 * its preset and only the members that differ from its kind's defaults (null otherwise).
 */
export interface PolitenessReportEntry {
  host: string
  preset: string
  requestsPerSecond: number | null
  maxConnections: number | null
  crawlDelay: 'honour' | 'ignore' | null
  adaptToLatency: boolean | null
  crawlWindow: 'source' | 'any' | null
  maxRetryAfterSeconds: number | null
  maxBackoffFactor: number | null
  breakerOff: boolean | null
  breakerThreshold: number | null
  breakerPauseMinutes: number | null
  robotsIgnoredReason: string | null
  userAgent: string | null
  renderPerHost: number | null
}

/** The crawl report's renderer counts of a `browser` source (worker `RenderSection`, connectors design §8.6). */
export interface RenderReport {
  rendered: number
  empty: number
  failures: number
  keptFromLastGood: number
}

/**
 * A crawl run's report (worker `CrawlSourceRunner`): completeness, the verdict, removal checks, demote counts, non-default
 * politeness and, for a `browser` source, the renderer counts.
 */
export interface CrawlReport {
  completeness?: { complete: boolean; reasons: string[]; seeds: number; seedsResolved: number; seedsFailed: number; incremental: boolean }
  verdict?: { runFailure: string | null; runDegradation: string | null }
  politeness?: PolitenessReportEntry[]
  render?: RenderReport
  /** `off`: the deployment runs without the malware scan, so the run's files were not scanned (#535, connectors §11). */
  malwareScan?: 'off' | string
  /** A continue run (#804): the run it went on from and the pages taken over (still open, failed), dropped by scope or depth, admitted. */
  continued?: { from: string; open: number; failed: number; dropped: number; admitted: number }
  [section: string]: unknown
}

export interface Run {
  run: RunSummary
  sourceId: string
  pipelineVersionId: string | null
  scheduledFor: IsoDateTime | null
  report: CrawlReport | null
  /** A continue run: the run it goes on from (#804). */
  continuesRunId: string | null
  /** The latest crawl run that ended with pages left or failed can be continued (#804). */
  canContinue: boolean
}

export interface RunItem {
  id: number
  createdAt: IsoDateTime
  documentId: string | null
  identity: string
  outcome: string
  reason: string | null
  strategy: string | null
  durationMs: number | null
  costUsd: number | null
}

export interface PiiReport {
  applies: boolean
  piiMode: string
  collectionClassification: Level
  documents: {
    documentId: string
    title: string | null
    url: string | null
    displayName: string | null
    state: 'held' | 'live'
    previousVersionLive: boolean
    counts: Record<string, number>
    rulesFired: number
    /** The types whose flag rule fired, as the worker stored them (#217): type names only, never values. */
    typesFired: string[]
    /** When an admin cleared the document (B-63); later versions stay covered while no count rises. */
    piiClearedAt: IsoDateTime | null
  }[]
  truncated: boolean
}

// ---------- Assistants (admin design §4.5, §4.9) ----------
/** `QuerySteps.answer.generalKnowledge` (chat design §7.3, admin design §5.5.4). */
export type GeneralKnowledge = 'off' | 'supplement' | 'fallback' | 'open'

export interface AssistantSummary {
  id: string
  slug: string
  name: string
  departmentId: string
  classification: Level
  state: string
  published: boolean
  hasDraft: boolean
  /** The published version's level; null before the first publish. */
  generalKnowledge: GeneralKnowledge | null
  /** The knowledge bases of the published version, else the draft's (admin design §5.5.4). */
  collections: { id: string; name: string }[]
  /** The answer model of the same version. */
  model: string | null
  /** The published version's number and date; null before the first publish. */
  publishedVersion: number | null
  publishedAt: IsoDateTime | null
  /** Questions of the last 7 Europe/Stockholm days, today included. */
  questions7d: number
}

/** An assistant's reranker (`QuerySteps.retrieval.rerank`, B-16); absent is off. */
export type Reranker = 'chat' | 'gateway'

export interface Assistant {
  id: string
  slug: string
  name: string
  description: string | null
  contactName: string | null
  contactEmail: string | null
  departmentId: string
  ownerTeamId: string | null
  classification: Level
  historyMode: string
  retentionDays: number
  state: string
  publishedVersion: number | null
  /** The published version's level; null before the first publish. */
  generalKnowledge: GeneralKnowledge | null
  /** The rerankers the platform has a model for (`chat`, `gateway`); empty when it has none, which is the usual setup. */
  rerankers: Reranker[]
  /** The platform answer ceiling `Settings.Chat.maxAnswerTokens` (chat §6.3), null when unset; shown on the Build tab when lower. */
  maxAnswerTokens?: number | null
  draft: { number: number; updatedAt: IsoDateTime; baseChanged: boolean; version: string } | null
  /** The assigned gateway key its calls run as (V39); null = the platform's credential key. */
  gatewayKeyId?: string | null
  audienceClientIds: string[]
  updatedAt: IsoDateTime
  updatedBy: string | null
  version: string
}

/** `GET /api/assistants/{id}/versions`, newest first. */
/** One line of a line diff (admin design §4.5, B-56). */
export interface DiffLine {
  op: 'equal' | 'insert' | 'delete'
  text: string
}

/** A text's line diff; `tooLong`: over 20,000 characters, so only whether it changed. */
export interface TextDiff {
  changed: boolean
  tooLong: boolean
  lines: DiffLine[]
}

/** `GET /api/assistants/{id}/versions/{n}/diff?against={m|draft}`: what changed from version `from` to `to`. */
export interface VersionDiff {
  from: number
  to: number
  toDraft: boolean
  changed: boolean
  /** Changed settings by name (`model`, `querySteps.retrieval.topK`, `notFound.links`); null when absent on that side. */
  settings: { name: string; from: unknown; to: unknown }[]
  collectionsAdded: { collectionId: string; name: string | null; tags: string[] }[]
  collectionsRemoved: { collectionId: string; name: string | null; tags: string[] }[]
  topicsChanged: { collectionId: string; name: string | null; from: string[]; to: string[] }[]
  instructions: TextDiff
  notFoundSv: TextDiff
  notFoundEn: TextDiff
}

/** A test question's expected document (admin design §7.4): `deleted` when it is gone, `unlinked` when no linked knowledge base holds it now. */
export interface ExpectedDocument {
  id: string
  title: string | null
  deleted: boolean
  unlinked: boolean
}

/** `GET /api/assistants/{id}/test-questions` (admin design §7.4, V33). */
export interface TestQuestion {
  id: string
  ordinal: number
  question: string
  expectedBehaviour: string
  expectedDocument: ExpectedDocument | null
  expectedPhrase: string | null
  updatedAt: IsoDateTime
  updatedBy: string | null
  version: string
}

export interface TestQuestionRequest {
  question: string
  expectedBehaviour: string
  expectedDocumentId: string | null
  expectedPhrase: string | null
  version?: string
}

/** A test-question run: how many as expected, and against the compared run (the previous version's). */
/** A finished nightly answer run (B-58): figures are 0–1, null when nothing was judged. */
export interface AnswerRunSummary {
  id: string
  version: number
  startedAt: IsoDateTime
  finishedAt: IsoDateTime
  questions: number
  found: number
  faithfulness: number | null
  citationPrecision: number | null
  wins: number | null
  losses: number | null
}

/** The Quality tab's answer evaluation (admin design §5.5.4): the last finished run, the one before, the live uncited rate. */
export interface AnswerEvaluation {
  enabled: boolean
  questionsPerNight: number
  latest: AnswerRunSummary | null
  previous: AnswerRunSummary | null
  live: { days: number; answered: number; uncited: number; rate: number | null }
}

export interface TestRunSummary {
  id: string
  version: number
  startedAt: IsoDateTime
  finishedAt: IsoDateTime | null
  questions: number
  asExpected: number | null
  better: number | null
  worse: number | null
  comparedRunId: string | null
  comparedVersion: number | null
  comparedAsExpected: number | null
}

/** One question in a run: `checks` is the expected document and phrase together (null when the question has neither). */
export interface TestResult {
  questionId: string
  question: string
  answer: string | null
  checks: boolean | null
  verdict: 'pass' | 'fail' | 'unsure' | null
  judgeNote: string | null
  asExpected: boolean
  change: 'better' | 'worse' | null
  expectedDocument: ExpectedDocument | null
  expectedPhrase: string | null
  expectedBehaviour: string | null
}

export interface TestRunReport {
  run: TestRunSummary
  results: TestResult[]
}

export interface AssistantVersionSummary {
  number: number
  state: 'draft' | 'published' | 'superseded' | string
  createdAt: IsoDateTime
  createdBy: string | null
  publishedAt: IsoDateTime | null
  publishedBy: string | null
  restoredFrom: number | null
}

/** `GET /api/data-protection/{level}`: what a level allows (plan §8a). */
export interface DataProtectionPolicy {
  classification: Level
  allowedHistoryModes: string[]
  defaultHistoryMode: string
  retentionDefaultDays: number
  retentionMaxDays: number
  version: string
}

/** `POST /api/assistants/{id}/copy` (admin design §4.5): the copy's name and department; slug and owner team default. */
export interface AssistantCopyRequest {
  name: string
  departmentId: string
  slug?: string | null
  ownerTeamId?: string | null
}

/** The copy and its draft's V-rule warnings for the target department. */
export interface AssistantCopyResult {
  assistant: Assistant
  warnings: RuleWarning[]
  /** what a copy into another department left out: links that department may not make, expected documents no kept link holds. */
  leftOut: { links: number; expectedDocuments: number }
}

export interface AssistantCreate {
  name: string
  departmentId: string
  classification: Level
  description: string | null
  contactName: string | null
  contactEmail: string | null
  /** The template the first draft starts from (copied, never linked); null: `Settings.Chat.defaultSystemPrompt`. */
  templateId?: string | null
}

/** `GET /api/assistant-templates` (admin design §4.5, P2): a starting point for new assistants. */
export interface AssistantTemplate {
  id: string
  name: string
  description: string
  systemPrompt: string
  notFound: Record<string, unknown>
  querySteps: Record<string, unknown>
  /** Offered when an assistant is created. */
  isActive: boolean
  updatedAt: IsoDateTime
  updatedBy: string | null
  version: string
}

export interface RuleWarning {
  code: string
  params: Record<string, string> | null
  affected: string[]
}

/** A collection an assistant version links; no tags = the whole collection (admin design §4.5). */
export interface CollectionLink {
  collectionId: string
  tags: string[]
}

/** `GET /api/assistants/{id}/versions/{n}`: one version's settings. */
export interface AssistantVersion {
  number: number
  state: 'draft' | 'published' | 'superseded' | string
  gatewayModel: string | null
  systemPrompt: string
  querySteps: Record<string, unknown>
  notFound: Record<string, unknown>
  collectionLinks: CollectionLink[]
  restoredFrom: number | null
  createdAt: IsoDateTime
  createdBy: string | null
  publishedAt: IsoDateTime | null
  publishedBy: string | null
}

export interface AssistantDraft {
  number: number
  gatewayModel: string | null
  systemPrompt: string
  querySteps: Record<string, unknown>
  notFound: Record<string, unknown>
  collectionLinks: CollectionLink[]
  warnings: RuleWarning[]
  updatedAt: IsoDateTime
  version: string
}

export interface ClientSummary {
  id: string
  name: string
  kind: string
  classificationCeiling: Level
  isEnabled: boolean
  /** The client's own UI (admin design §5.4 step 13). */
  portalUrl?: string | null
  /** The deployment's own client (identity design §5.2): made by kalla-migrate, its key the deployment's. */
  isManagedByDeployment: boolean
  /** Its keys that work now (admin design §5.9). */
  activeKeys: number
  /** The latest use of any of its keys; null before the app's first call (admin design §5.9.1). */
  lastUsedAt: IsoDateTime | null
}

// ---------- Clients and keys (admin design §5.9) ----------
export type ClientKind = 'open-webui' | 'widget' | 'integration' | 'entra-app' | 'mcp-client' | 'own-client' | 'teams' | 'internal'

/** An assistant with a `client` audience row for the client. */
export interface ClientAssistant {
  id: string
  name: string
  departmentId: string
  published: boolean
  /** Its model name in `GET /v1/models`. */
  slug: string
  classification: Level
}

/** `GET /api/clients/connection`: kalla-api's public address, or null when the deployment names none (admin design §5.9.1). */
export interface ClientConnection {
  apiBaseUrl: string | null
}

/** `GET /api/clients/{id}` (`ClientDto`). */
export interface Client {
  id: string
  name: string
  kind: string
  oAuthClientId: string | null
  classificationCeiling: Level
  storesHistory: boolean
  clientRetentionDays: number | null
  allowedOrigins: string[]
  toolPolicy: string
  portalUrl: string | null
  isEnabled: boolean
  /** The deployment's own client (identity design §5.2): its key is the deployment's, not changed here. */
  isManagedByDeployment: boolean
  updatedAt: IsoDateTime
  updatedBy: string | null
  version: string
  assistants: ClientAssistant[]
}

/** `ClientRequest`; an update carries the `version` it read. */
export interface ClientRequest {
  name: string
  kind: string
  oAuthClientId: string | null
  classificationCeiling: Level
  storesHistory: boolean
  clientRetentionDays: number | null
  allowedOrigins: string[]
  toolPolicy?: string
  portalUrl: string | null
  isEnabled: boolean
  version?: string
}

/** `KeyDto`: never its secret. `status`: `active`, `in-grace-period`, `expired`, `revoked` or `disabled`. */
export interface ApiKey {
  id: string
  clientId: string
  clientName: string
  teamId: string | null
  kind: string
  name: string
  description: string | null
  prefix: string
  status: string
  assistantIds: string[]
  collectionIds: string[]
  allowedIpRanges: string[]
  requestsPerMinute: number | null
  isEnabled: boolean
  createdAt: IsoDateTime
  createdBy: string | null
  expiresAt: IsoDateTime | null
  revokedAt: IsoDateTime | null
  graceUntil: IsoDateTime | null
  rotatedToKeyId: string | null
  /** Null before the key's first call. */
  lastUsedAt: IsoDateTime | null
}

/** `KeyFields`: empty scopes are the client's audiences only. */
export interface KeyRequest {
  name: string
  description: string | null
  expiresAt: IsoDateTime | null
  scopes: { assistantIds: string[]; collectionIds: string[] }
  allowedIpRanges: string[]
  requestsPerMinute: number | null
  isEnabled: boolean
}

export interface KeyCreateRequest extends KeyRequest {
  clientId: string
  teamId: string | null
}

/** A new key with its secret, shown this once. */
export interface KeyCreated {
  key: ApiKey
  secret: string
}

export interface KeyRotated extends KeyCreated {
  previous: ApiKey
}

export type KeyRotationMode = 'RevokeImmediately' | 'Grace24Hours'

/** `POST /api/clients` (`ClientCreatedDto`): the key is null without `firstKey`. */
export interface ClientCreated {
  client: Client
  key: KeyCreated | null
}

/** An assigned gateway key of the mirror (`GET /api/gateway/keys`, admin design §4.5): read-only settings, never a secret. */
export interface GatewayKey {
  id: string
  name: string
  teamId: string
  teamName: string | null
  departmentId: string | null
  status: string
  ownerActive: boolean
  expiresAt: IsoDateTime | null
  /** Empty = every model. */
  allowedModels: string[]
  /** Empty = every residency. */
  allowedResidencies: string[]
  piiPolicy: string
  contentGuardPolicy: string
  requestsPerMinute: number | null
  tokensPerMinute: number | null
  maxConcurrentRequests: number | null
  maxOutputTokens: number | null
  hasSystemPrompt: boolean
  usable: boolean
  /** V39's status part: active, owner active and usable. */
  selectable: boolean
  /** Why it cannot be chosen, when `selectable` is false. */
  reason: string | null
  syncedAt: IsoDateTime
  /** The assistants and live knowledge bases that hold the key (#642: the Cost card's shared-key note). */
  usedBy: number
}

/** The key picker's list and the gateway admin UI address for links (null when unknown). */
export interface GatewayKeyList {
  adminUrl: string | null
  keys: GatewayKey[]
}

export interface GatewayModel {
  name: string
  /** `model` (a deployment) or `route` (a manual route alias). */
  kind: string
  /** What the model serves (data-model §2.1); route aliases are `chat`. Only chat models answer. */
  modelKind: 'chat' | 'embedding' | 'rerank'
  residencies: string[]
  /**
   * Whether the platform's key may use it at the asked ceiling (#447): `false` greys it out in a picker, `null` when the
   * gateway could not be asked (every model stays choosable).
   */
  available?: boolean | null
  /** The model's limits in tokens (Decisions "Answer length from the model"); null when unknown, a platform admin enters them. */
  contextWindow?: number | null
  maxOutputTokens?: number | null
}

export interface AssistantTestResult {
  target: string
  versionNumber: number
  retrieval: {
    collections: { id: string; name: string; published: boolean }[]
    coverage: { status: string } | null
    degraded: string | null
    candidates: { rank: number; documentId: string; title: string; urlOrFileName: string | null; headingPath: string; text: string; cited: boolean }[]
  }
  condense: { used: boolean; outcome: string | null }
  answer: { raw: string | null; delivered: string; outcome: string; language: string; citations: { number: number; documentId: string; title: string }[] }
  gateway: {
    model: string
    residency: string
    latencyMs: number | null
    inputTokens: number | null
    outputTokens: number | null
    /** The `max_tokens` sent, null when left out (admin design §4.9). */
    maxTokens?: number | null
    maxTokensSource?: 'override' | 'model' | 'provider' | 'ceiling' | null
    /** `length`: the answer stopped at the limit. */
    finishReason?: string | null
    /** The sum over every model call of the turn, in USD (admin design §4.9). */
    costUsd: number
    /** True when any part of `costUsd` is an estimate from the mirrored prices. */
    costEstimated: boolean
  }
  durationMs: number
  /** The level the test ran at: the stricter of the asked `generalKnowledge` and the tested version's (#726, admin design §4.9). */
  generalKnowledge: GeneralKnowledge
}

/** `GET /api/collections/{id}/setup`: the setup checklist computed from state (admin design §5.4). */
export interface CollectionSetup {
  collectionId: string
  name: string
  sources: number
  firstSyncDone: boolean
  syncRunning: boolean
  testQuestions: number
  evaluated: boolean
  evaluating: boolean
  published: boolean
  assistantId: string | null
  assistantPublished: boolean
  available: boolean
  done: boolean
}

/** A published assistant's last 7 days on Home (admin design §5.5.1): summed over the days and every client. */
export interface LiveAssistant {
  assistantId: string
  name: string
  departmentId: string
  /** The published version's number. */
  publishedVersion: number
  questions: number
  notFound: number
  thumbsUp: number
  thumbsDown: number
  /** The gateway's cost plus the estimate (§8.6). */
  costUsd: number
}

/** Runs by outcome: done, with remarks (degraded or held for deletion confirmation), failed, running. */
export interface RunCounts {
  succeeded: number
  degraded: number
  failed: number
  running: number
}

/** A gateway sync's last success and its failed runs in a row since. */
export interface SyncHealth {
  lastSuccessAt: string | null
  consecutiveFailures: number
}

/** Health on Home: the runs of the last 24 hours per department, the job queue, and the organisation and model syncs. */
export interface OverviewHealth {
  departments: { departmentId: string; departmentName: string; runs: RunCounts }[]
  queue: { waiting: number; oldestQueuedAt: string | null }
  organisationSync: SyncHealth
  modelSync: SyncHealth
}

/** One Needs-attention item (admin design §5.5.1): its kind, the object it is about and the figures its text names. */
export interface AttentionItem {
  kind: string
  objectType: 'collection' | 'source' | 'assistant' | 'client' | 'pipeline' | 'setting' | 'sync'
  objectId: string | null
  objectName: string
  departmentId: string | null
  since: IsoDateTime | null
  details: Record<string, string>
}

/** `GET /api/overview`: Home (admin design §5.5.1): Live, Health, whether any collection is in scope, and the checklists not done yet. */
export interface Overview {
  hasCollections: boolean
  setup: CollectionSetup[]
  /** A platform admin, and no active default embedding profile yet (the platform's first-run step). */
  needsEmbeddingProfile: boolean
  /** The caller's published, active assistants, most questions first. */
  live: LiveAssistant[]
  health: OverviewHealth
  /** A platform admin's reminder (admin design §5.11): the wizard-stored client secrets that expire within 30 days, expired ones included. */
  secretExpiry: SecretExpiry[]
  /** A platform admin's deployment notices (admin design §5.5.1): what the person who runs the deployment should tidy. */
  deploymentNotices: DeploymentNotice[]
  /** Needs attention (B-59): every item of the caller's scope. */
  attention: AttentionItem[]
}

/** A deployment notice on Home (`DeploymentNotice`); a code this UI doesn't know is not shown. */
export interface DeploymentNotice {
  /** `retired_variables`: deployment variables the stack still sets and this version no longer reads. */
  code: string
  names: string[]
}

/** A client secret stored by the setup wizard that expires soon or has expired (`SecretExpiry`). */
export interface SecretExpiry {
  which: SetupTarget
  /** `yyyy-mm-dd`. */
  expiresOn: string
}

/** `GET /api/audit`: an entry of the audit log (admin design §9.1). */
export interface AuditSummary {
  id: number
  timestamp: IsoDateTime
  actor: string
  actorName: string | null
  action: string
  entityType: string
  entityId: string | null
  /** The object's current name, else the name in the entry's snapshot (a deleted object), else null (admin design §9.1). */
  entityName: string | null
  /** Whether the object still exists, for the types the list looks up; null for the others. */
  entityExists: boolean | null
  service: string | null
  departmentId: string | null
  classification: string | null
  configVersion: number | null
  reason: string | null
}

/** `GET /api/audit/{id}`: the entry with its before/after snapshot as JSON text (without the list's object name). */
export interface AuditDetail extends Omit<AuditSummary, 'entityName' | 'entityExists'> {
  recordedAt: IsoDateTime
  actorRoles: string | null
  actorDepartments: string | null
  details: string | null
  sourceAddress: string | null
  requestId: string | null
  userAgent: string | null
}

/** `GET /api/feedback`: a rating without content (admin design §8.1). */
export interface FeedbackItem {
  id: string
  createdAt: IsoDateTime
  rating: number
  score: number | null
  reasonCategory: string | null
  reviewState: 'open' | 'reviewed' | 'dismissed' | string
  hasComment: boolean
  source: string
  assistantId: string
  assistantName: string
  assistantVersion: number | null
  clientId: string
  clientName: string | null
  outcome: string
  coverage: string | null
  language: string | null
  citedDocuments: { id: string; title: string | null }[]
}

/** `GET /api/feedback/summary`: the period's totals and the Open WebUI import health. */
export interface FeedbackSummary {
  totals: { answers: number; notFound: number; thumbsUp: number; thumbsDown: number }
  /** The last day with an answer, of any period; null before the first (the assistant's Usage tab). */
  lastUsedOn: string | null
  imports: {
    clientId: string
    clientName: string
    lastRunAt: IsoDateTime | null
    lastOutcome: string | null
    unmapped: number
    failedRuns: number
    alert: boolean
  }[]
}

/** `POST /api/access/explain` (identity design §4.9): the first failing policy step and its stable reason. */
export interface AccessExplanation {
  allowed: boolean
  step: string
  reason: string
  status: number
  effectiveLevel: string | null
  maxResidency: string | null
  configVersion: number
}

export interface ExplainAccessRequest {
  clientId: string
  assistantId: string
  subject: { kind: string; id: string; departments?: string[]; groups?: string[] }
}

/** The cost report's period unit (admin design §8.6): Europe/Stockholm days or calendar months. */
export type CostGranularity = 'day' | 'month'

/** A cost query (admin design §8.6): inclusive dates; absent, the last 30 days by day. */
export interface CostQuery {
  from?: string
  to?: string
  granularity?: CostGranularity
}

/** The summed fields of a cost row (admin design §8.6): the gateway's cost apart from the estimate of calls without one. */
export interface CostTotals {
  calls: number
  inputTokens: number
  cachedInputTokens: number
  outputTokens: number
  costUsd: number
  estimatedCostUsd: number
  estimatedCalls: number
}

/** An assistant's or knowledge base's cost per period, step and (assistants) client; `clientName` is `admin-console` for the test panel. */
export interface CostRow extends CostTotals {
  /** `yyyy-MM-dd`; a month's first day by month. */
  period: string
  step: string
  clientId: string | null
  clientName: string | null
}

/** `GET /api/assistants/{id}/costs`, `GET /api/collections/{id}/costs`. */
export interface ObjectCosts {
  from: string
  to: string
  granularity: CostGranularity
  rows: CostRow[]
  totals: CostTotals
}

/**
 * An assistant's turns and their full cost per period, outcome and client (Decisions "Cost per outcome"): the gateway's cost apart
 * from the estimate; `clientName` is `admin-console` for the test panel.
 */
export interface OutcomeCostRow {
  /** `yyyy-MM-dd`; a month's first day by month. */
  period: string
  /** `Messages.Outcome`: `answered`, `answered-general`, `not-found`, `unavailable`, `refused`, `error`, `cancelled`. */
  outcome: string
  clientId: string
  clientName: string | null
  turns: number
  costUsd: number
  estimatedCostUsd: number
}

/** `GET /api/assistants/{id}/costs`: the cost rows and the turns by outcome (admin design §8.6). */
export interface AssistantCosts extends ObjectCosts {
  outcomes: OutcomeCostRow[]
}

/**
 * One nearest document of the content-gap report (admin design §8.7): the knowledge base's name and the document's title, each
 * null when it is gone or deleted (`deleted`: the document is), or `noHit` (the zero ids) when the search found nothing, or
 * `notShared` (both null) when the knowledge base is another department's that is no longer shared with the assistant's; `canOpen`
 * when the caller's departments own the knowledge base; the turns that ended not found and answered from general knowledge.
 */
export interface ContentGapRow {
  collectionId: string
  documentId: string
  noHit: boolean
  collectionName: string | null
  documentTitle: string | null
  deleted: boolean
  notShared: boolean
  canOpen: boolean
  notFound: number
  answeredGeneral: number
}

/** `GET /api/assistants/{id}/gaps` (admin design §8.7): the period's turns, the two counts and the nearest documents, most first (at most 200). */
export interface ContentGapReport {
  from: string
  to: string
  turns: number
  notFound: number
  answeredGeneral: number
  documents: ContentGapRow[]
}

/**
 * One document of `GET /api/collections/{id}/feedback` (admin design §4.5, §8.8): its title (null when `deleted`), the downvotes
 * and upvotes of the rated answers that cited it, and how many assistants' answers those were (never their names).
 */
export interface DocumentFeedbackRow {
  documentId: string
  title: string | null
  deleted: boolean
  thumbsDown: number
  thumbsUp: number
  assistants: number
}

/** `GET /api/collections/{id}/feedback` (admin design §8.8): the period and its documents, most downvotes first (at most 200). */
export interface DocumentFeedbackReport {
  from: string
  to: string
  documents: DocumentFeedbackRow[]
}

/** A row of `GET /api/costs`: per period, department, object, step and client; `deleted` when the object is gone. */
export interface DepartmentCostRow extends CostTotals {
  period: string
  departmentId: string
  departmentName: string | null
  kind: 'assistant' | 'collection'
  objectId: string
  objectName: string | null
  deleted: boolean
  step: string
  clientId: string | null
  clientName: string | null
}

/** `GET /api/costs` (admin design §8.6). */
export interface CostReport {
  from: string
  to: string
  granularity: CostGranularity
  rows: DepartmentCostRow[]
  totals: CostTotals
}

/** `GET /api/costs` filters. */
export interface CostReportQuery extends CostQuery {
  departmentId?: string
  kind?: 'assistant' | 'collection'
}

/** A budget that applies to a gateway key (`GET /api/gateway/keys/{id}/budget`, admin design §4.5; the gateway's Integration API). */
export interface KeyBudget {
  scope: 'key' | 'team' | 'department'
  unit: 'usd' | 'tokens'
  limit: number
  spent: number
  percentUsed: number
  exhausted: boolean
  residency: string | null
  period: string
  periodStart: IsoDateTime
  periodEnd: IsoDateTime
}

/** A key's usage per period and step as the gateway counted it (`GET /api/gateway/keys/{id}/usage`, admin design §8.6). */
export interface KeyUsageRow {
  period: string
  step: string | null
  requests: number
  successfulRequests: number
  inputTokens: number
  cachedInputTokens: number
  outputTokens: number
  costUsd: number
}

export interface KeyUsage {
  from: string
  to: string
  granularity: CostGranularity
  rows: KeyUsageRow[]
}

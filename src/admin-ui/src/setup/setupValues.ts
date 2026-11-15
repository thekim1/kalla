import type {
  OidcWizardValues, OpenWebUiCompleteRequest, OpenWebUiSignIn, OpenWebUiTest, OpenWebUiTestRequest, OpenWebUiWizardValues, SetupCompleteRequest, SetupTest,
  SetupValuesRequest, SignInSecretStatus, SignInSettings, SignInValueSource,
} from '@/api/types'

/**
 * The setup wizard's values (admin design §5.11 steps 3 and 4, identity design §19): the forms of rag-admin's sign-in and of Open
 * WebUI's, what they send, and when a test still counts. Only what is typed in the wizard is sent: a field left empty (the default)
 * or set by the deployment is sent as null.
 */

/**
 * Every reason a test sign-in ends with, as the admin API names them: the refusals of `POST /bff/setup/test` and the
 * callback's results (`SetupTestReasons` in SetupProvider.cs, and `TenantCheck`'s `wrong_tenant` and `guest_refused`).
 * messages.ts has a text for each (messages.spec.ts).
 */
export const SETUP_TEST_REASONS = [
  'authority_invalid',
  'authority_unreachable',
  'tls',
  'redirected',
  'not_discovery',
  'issuer_mismatch',
  'insecure_endpoint',
  'par_required',
  'keys_unavailable',
  'stored_secret_unreadable',
  'provider_error',
  'token_refused',
  'token_endpoint_unreachable',
  'id_token_missing',
  'id_token_invalid',
  'nonce_mismatch',
  'secret_required',
  'deployment_secret_without_authority',
  'provider_url_invalid',
  'client_refused',
  'client_unverified',
  'browser_mismatch',
  'session_expired',
  'unknown_test',
  'wrong_tenant',
  'guest_refused',
] as const

/**
 * The reasons of `setup_test_required` from `POST /bff/setup/complete` (SetupModeEndpoints.cs): rag-admin's, and Open WebUI's, which
 * adds `admin_test_required` (no passed test sign-in by a platform admin in this session).
 */
export const SETUP_COMPLETE_REASONS = ['unknown_test', 'not_latest', 'not_passed', 'not_platform_admin', 'values_changed', 'admin_test_required'] as const

/** `Oidc:RefuseGuests`'s default (identity design §1.4): refusing guests is sent as null. */
export const REFUSE_GUESTS_DEFAULT = true

export const TEXT_FIELDS = ['authority', 'clientId', 'roleClaim', 'departmentClaim', 'teamClaim', 'groupClaim', 'tenantId'] as const
export type TextFieldName = (typeof TEXT_FIELDS)[number]
/** The values a deployment can set (and so lock). */
export type SignInField = TextFieldName | 'extraScopes' | 'refuseGuests' | 'clientSecret'

/**
 * A client secret's part of a form, rag-admin's and Open WebUI's alike: the secret is typed only; it is never prefilled and lives
 * nowhere but here.
 */
export interface SecretFields {
  /** `yyyy-mm-dd`, or empty. */
  clientSecretExpiresOn: string
  clientSecret: string
  /** Test without a secret (a public client); saving removes the stored one. */
  removeSecret: boolean
}

/** The wizard's form of rag-admin's sign-in. */
export interface SetupForm extends SecretFields {
  authority: string
  clientId: string
  roleClaim: string
  departmentClaim: string
  teamClaim: string
  groupClaim: string
  tenantId: string
  /** Separated by spaces (or commas). */
  extraScopes: string
  refuseGuests: boolean
}

export type ProviderPreset = 'keycloak' | 'entra'

/** Whether the deployment sets `field` of `values` (rag-admin's or Open WebUI's sign-in values), so the wizard shows it locked. */
export function isLocked<K extends string>(values: Record<K, { source: SignInValueSource }>, field: K): boolean {
  return values[field].source === 'deployment'
}

/**
 * The form for `signIn` (`GET /bff/setup/state`): a deployment's value is shown (locked), the wizard's own value is filled
 * in, a default is left empty. With a `tested` test (a reload), the values that test was made with are filled in instead, so
 * the test still counts.
 */
export function newForm(signIn: SignInSettings, tested: SetupTest | null = null): SetupForm {
  const values = tested?.values ?? null
  const text = (field: TextFieldName): string => {
    if (isLocked(signIn, field)) return signIn[field].value ?? ''
    if (values) return values[field] ?? ''
    return signIn[field].source === 'wizard' ? (signIn[field].value ?? '') : ''
  }
  const scopes = isLocked(signIn, 'extraScopes') ? signIn.extraScopes.value
    : values ? (values.extraScopes ?? [])
    : signIn.extraScopes.source === 'wizard' ? signIn.extraScopes.value : []
  const refuseGuests = isLocked(signIn, 'refuseGuests') ? signIn.refuseGuests.value
    : values ? (values.refuseGuests ?? REFUSE_GUESTS_DEFAULT)
    : signIn.refuseGuests.value
  return {
    authority: text('authority'),
    clientId: text('clientId'),
    roleClaim: text('roleClaim'),
    departmentClaim: text('departmentClaim'),
    teamClaim: text('teamClaim'),
    groupClaim: text('groupClaim'),
    tenantId: text('tenantId'),
    extraScopes: scopes.join(' '),
    refuseGuests,
    clientSecretExpiresOn: values ? (values.clientSecretExpiresOn ?? '') : (signIn.clientSecret.expiresOn ?? ''),
    clientSecret: '',
    removeSecret: tested?.clientSecret === 'removed',
  }
}

/** The preset a form looks like: a tenant id is Entra ID's. */
export function presetOf(form: SetupForm): ProviderPreset {
  return form.tenantId.trim() ? 'entra' : 'keycloak'
}

/**
 * Applies a preset to the Advanced values that are not locked. Keycloak is all defaults. Entra ID's claim names are the
 * defaults too (`roles`, `groups`; identity design §1.4, §2.5): it keeps the tenant id, which it needs, and refuses guests.
 */
export function applyPreset(form: SetupForm, signIn: SignInSettings, preset: ProviderPreset): void {
  for (const field of ['roleClaim', 'departmentClaim', 'teamClaim', 'groupClaim'] as const) {
    if (!isLocked(signIn, field)) form[field] = ''
  }
  if (!isLocked(signIn, 'extraScopes')) form.extraScopes = ''
  if (preset === 'keycloak' && !isLocked(signIn, 'tenantId')) form.tenantId = ''
  if (!isLocked(signIn, 'refuseGuests')) form.refuseGuests = REFUSE_GUESTS_DEFAULT
}

function clean(value: string | null | undefined): string | null {
  const trimmed = value?.trim()
  return trimmed ? trimmed : null
}

function scopesOf(text: string): string[] | null {
  const scopes = [...new Set(text.split(/[\s,]+/).filter(Boolean))]
  return scopes.length ? scopes : null
}

/** The values the form sends, without the secret (`OidcWizardValues`). */
export function valuesOf(form: SetupForm, signIn: SignInSettings): OidcWizardValues {
  const text = (field: TextFieldName) => (isLocked(signIn, field) ? null : clean(form[field]))
  return {
    authority: text('authority'),
    clientId: text('clientId'),
    roleClaim: text('roleClaim'),
    departmentClaim: text('departmentClaim'),
    teamClaim: text('teamClaim'),
    groupClaim: text('groupClaim'),
    extraScopes: isLocked(signIn, 'extraScopes') ? null : scopesOf(form.extraScopes),
    tenantId: text('tenantId'),
    refuseGuests: isLocked(signIn, 'refuseGuests') || form.refuseGuests === REFUSE_GUESTS_DEFAULT ? null : form.refuseGuests,
    clientSecretExpiresOn: expiryOf(form, signIn.clientSecret),
  }
}

/**
 * A secret as `POST test` takes it, for either target: null keeps the stored one (or uses the deployment's), "" tests without one,
 * a value tests with that value.
 */
export function typedSecretOf(fields: SecretFields, secret: SignInSecretStatus): string | null {
  if (secret.source === 'deployment') return null
  if (fields.removeSecret) return ''
  return fields.clientSecret.trim() ? fields.clientSecret : null
}

/** The expiry date sent with a secret: it belongs to a secret kept by the wizard, so a deployment's secret, or none, has none. */
function expiryOf(fields: SecretFields, secret: SignInSecretStatus): string | null {
  return secret.source === 'deployment' || fields.removeSecret ? null : clean(fields.clientSecretExpiresOn)
}

/** rag-admin's secret as `POST test` takes it (`typedSecretOf`). */
export function secretOf(form: SetupForm, signIn: SignInSettings): string | null {
  return typedSecretOf(form, signIn.clientSecret)
}

export function testRequestOf(form: SetupForm, signIn: SignInSettings): SetupValuesRequest {
  return { ...valuesOf(form, signIn), clientSecret: secretOf(form, signIn) }
}

/** `POST complete` takes the tested values exactly, as the test answered them, and the test's id. */
export function completeRequestOf(test: SetupTest & { values: OidcWizardValues }): SetupCompleteRequest {
  return { ...normalized(test.values), testId: test.testId }
}

/** As the API's `OidcWizardValues.Normalized()`: trimmed, empty as null, scopes without repeats. */
export function normalized(values: OidcWizardValues): OidcWizardValues {
  const scopes = [...new Set((values.extraScopes ?? []).map(clean).filter((s): s is string => s !== null))]
  return {
    authority: clean(values.authority),
    clientId: clean(values.clientId),
    roleClaim: clean(values.roleClaim),
    departmentClaim: clean(values.departmentClaim),
    teamClaim: clean(values.teamClaim),
    groupClaim: clean(values.groupClaim),
    extraScopes: scopes.length ? scopes : null,
    tenantId: clean(values.tenantId),
    refuseGuests: values.refuseGuests ?? null,
    clientSecretExpiresOn: clean(values.clientSecretExpiresOn),
  }
}

export function sameValues(a: OidcWizardValues, b: OidcWizardValues): boolean {
  return JSON.stringify(normalized(a)) === JSON.stringify(normalized(b))
}

/**
 * Whether a finished test no longer stands for the form: a value differs from the tested ones, or the secret was changed
 * since the test started (`secretEdited`; the secret is not in the test's values).
 */
export function isStale(test: SetupTest | null, form: SetupForm, signIn: SignInSettings, secretEdited: boolean): boolean {
  if (!test || test.status === 'pending') return false
  return !test.values || secretEdited || !sameValues(valuesOf(form, signIn), test.values)
}

/** Finish is offered for a passed test by a platform admin that still stands for the form. */
export function canFinish(test: SetupTest | null, stale: boolean): test is SetupTest & { values: OidcWizardValues } {
  return !!test && test.status === 'passed' && test.result?.isPlatformAdmin === true && !!test.values && !stale
}

// ---------- Open WebUI (admin design §5.11 step 4, identity design §19 "Open WebUI") ----------

export const OPENWEBUI_TEXT_FIELDS = ['providerUrl', 'clientId', 'providerName'] as const
export type OpenWebUiTextField = (typeof OPENWEBUI_TEXT_FIELDS)[number]

/** The wizard's form of Open WebUI's sign-in. */
export interface OpenWebUiForm extends SecretFields {
  /** The discovery document's address; empty follows the sign-in provider. */
  providerUrl: string
  clientId: string
  providerName: string
}

/** As `newForm`, for Open WebUI: a deployment's value locked, the wizard's own or the checked one filled in, a default left empty. */
export function newOpenWebUiForm(signIn: OpenWebUiSignIn, tested: OpenWebUiTest | null = null): OpenWebUiForm {
  const values = tested?.values ?? null
  const text = (field: OpenWebUiTextField): string => {
    if (isLocked(signIn, field)) return signIn[field].value ?? ''
    if (values) return values[field] ?? ''
    return signIn[field].source === 'wizard' ? (signIn[field].value ?? '') : ''
  }
  return {
    providerUrl: text('providerUrl'),
    clientId: text('clientId'),
    providerName: text('providerName'),
    clientSecretExpiresOn: values ? (values.clientSecretExpiresOn ?? '') : (signIn.clientSecret.expiresOn ?? ''),
    clientSecret: '',
    removeSecret: tested?.clientSecret === 'removed',
  }
}

/** What happened to Open WebUI's values in this session: saved, skipped, set by the deployment (nothing to save), or none of them. */
export type OpenWebUiOutcome = 'saved' | 'skipped' | 'deployment' | 'none'

/** Whether the deployment sets every one of Open WebUI's values: the step then has nothing to save. */
export function openWebUiAllLocked(signIn: OpenWebUiSignIn): boolean {
  return OPENWEBUI_TEXT_FIELDS.every((field) => isLocked(signIn, field)) && isLocked(signIn, 'clientSecret')
}

/** The values the Open WebUI form sends, without the secret (`OpenWebUiOidcValues`). */
export function openWebUiValuesOf(form: OpenWebUiForm, signIn: OpenWebUiSignIn): OpenWebUiWizardValues {
  const text = (field: OpenWebUiTextField) => (isLocked(signIn, field) ? null : clean(form[field]))
  return {
    providerUrl: text('providerUrl'),
    clientId: text('clientId'),
    providerName: text('providerName'),
    clientSecretExpiresOn: expiryOf(form, signIn.clientSecret),
  }
}

/** `POST test` for Open WebUI: the target and its own members only (a member of the other target is a 400). */
export function openWebUiTestRequestOf(form: OpenWebUiForm, signIn: OpenWebUiSignIn): OpenWebUiTestRequest {
  return { target: 'open-webui', ...openWebUiValuesOf(form, signIn), clientSecret: typedSecretOf(form, signIn.clientSecret) }
}

/** As the API's `OpenWebUiOidcValues.Normalized()`: trimmed, empty as null. */
export function normalizedOpenWebUi(values: OpenWebUiWizardValues): OpenWebUiWizardValues {
  return {
    providerUrl: clean(values.providerUrl),
    clientId: clean(values.clientId),
    providerName: clean(values.providerName),
    clientSecretExpiresOn: clean(values.clientSecretExpiresOn),
  }
}

/** `POST complete` for Open WebUI: the checked values exactly, as the check answered them, and the check's id. */
export function openWebUiCompleteRequestOf(test: OpenWebUiTest & { values: OpenWebUiWizardValues }): OpenWebUiCompleteRequest {
  return { target: 'open-webui', ...normalizedOpenWebUi(test.values), testId: test.testId }
}

/** As `isStale`, for Open WebUI's check (which is never pending). */
export function isOpenWebUiStale(test: OpenWebUiTest | null, form: OpenWebUiForm, signIn: OpenWebUiSignIn, secretEdited: boolean): boolean {
  if (!test) return false
  const now = normalizedOpenWebUi(openWebUiValuesOf(form, signIn))
  return !test.values || secretEdited || JSON.stringify(now) !== JSON.stringify(normalizedOpenWebUi(test.values))
}

/** Save is offered for a check that found the discovery document, was not refused, and still stands for the form. */
export function canSaveOpenWebUi(test: OpenWebUiTest | null, stale: boolean): test is OpenWebUiTest & { values: OpenWebUiWizardValues } {
  return !!test && test.status === 'passed' && test.result?.discovery === 'ok' && test.result.clientAccepted !== false && !!test.values && !stale
}

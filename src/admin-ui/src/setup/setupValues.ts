import type { OidcWizardValues, SetupCompleteRequest, SetupTest, SetupValuesRequest, SignInSettings } from '@/api/types'

/**
 * The setup wizard's values (admin design §5.11 step 3, identity design §19): the form, what it sends, and when a test still
 * counts. Only what is typed in the wizard is sent: a field left empty (the default) or set by the deployment is sent as null.
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
  'browser_mismatch',
  'session_expired',
  'unknown_test',
  'wrong_tenant',
  'guest_refused',
] as const

/** The reasons of `setup_test_required` from `POST /bff/setup/complete` (SetupModeEndpoints.cs). */
export const SETUP_COMPLETE_REASONS = ['unknown_test', 'not_latest', 'not_passed', 'not_platform_admin', 'values_changed'] as const

/** `Oidc:RefuseGuests`'s default (identity design §1.4): refusing guests is sent as null. */
export const REFUSE_GUESTS_DEFAULT = true

export const TEXT_FIELDS = ['authority', 'clientId', 'roleClaim', 'departmentClaim', 'teamClaim', 'groupClaim', 'tenantId'] as const
export type TextFieldName = (typeof TEXT_FIELDS)[number]
/** The values a deployment can set (and so lock). */
export type SignInField = TextFieldName | 'extraScopes' | 'refuseGuests' | 'clientSecret'

/** The wizard's form. The secret is typed only; it is never prefilled and lives nowhere but here. */
export interface SetupForm {
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
  /** `yyyy-mm-dd`, or empty. */
  clientSecretExpiresOn: string
  clientSecret: string
  /** Test without a secret (a public client); finishing removes the stored one. */
  removeSecret: boolean
}

export type ProviderPreset = 'keycloak' | 'entra'

export function isLocked(signIn: SignInSettings, field: SignInField): boolean {
  return signIn[field].source === 'deployment'
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
    // The expiry date belongs to a secret kept by the wizard; a deployment's secret has none here.
    clientSecretExpiresOn: isLocked(signIn, 'clientSecret') || form.removeSecret ? null : clean(form.clientSecretExpiresOn),
  }
}

/** The secret as `POST test` takes it: null keeps the stored one, "" tests without one, a value tests with that value. */
export function secretOf(form: SetupForm, signIn: SignInSettings): string | null {
  if (isLocked(signIn, 'clientSecret')) return null
  if (form.removeSecret) return ''
  return form.clientSecret.trim() ? form.clientSecret : null
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

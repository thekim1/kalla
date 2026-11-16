import type {
  AssistantSummary, BffUser, Collection, CollectionSummary, CollectionUser, Department, OidcWizardValues, OpenWebUiSignIn, OpenWebUiTest, OpenWebUiWizardValues, SetupOpenWebUiState, SetupState, SetupTest, SignInSettings,
} from '@/api/types'

/** A signed-in user of `/bff/user`; override what the test cares about. */
export function bffUser(overrides: Partial<BffUser> = {}): BffUser {
  return {
    isAuthenticated: true,
    environment: 'Development',
    name: 'Anna Andersson',
    email: 'anna@example.invalid',
    roles: ['rag-viewer'],
    departmentCodes: ['1000'],
    departmentSources: [{ code: '1000', source: 'claim' }],
    sessionExpiresAt: null,
    signIn: { configured: true },
    setup: { open: false },
    ...overrides,
  }
}

let counter = 0

/** A synced department with one team. */
export function department(overrides: Partial<Department> = {}): Department {
  counter += 1
  return {
    id: `d${counter}`,
    name: `Förvaltning ${counter}`,
    costCenterCode: `${1000 * counter}`,
    isActive: true,
    syncedAt: '2026-10-30T12:00:00Z',
    teams: [{ id: `t${counter}`, name: `Team ${counter}`, code: null, isActive: true }],
    ...overrides,
  }
}

/** The owner's detail of a knowledge base (`GET /api/collections/{id}`). */
export function collection(overrides: Partial<Collection> = {}): Collection {
  return {
    id: 'c1', slug: 'bygglov', name: 'Bygglov', departmentId: 'd1', classification: 'internal', publishedAt: null, ownerTeamId: null, shared: false,
    sharedWith: 'department', sharedWithDepartmentIds: [], contactName: null, contactEmail: null, embeddingProfileId: 'p', retrievalSettings: {}, documentCount: 0, usedBy: [],
    updatedAt: '2026-11-01T10:00:00Z', updatedBy: null, version: '1', ...overrides,
  }
}

/** A list row of `GET /api/collections?view=`. */
export function collectionSummary(overrides: Partial<CollectionSummary> = {}): CollectionSummary {
  return {
    id: 'c1', slug: 'bygglov', name: 'Bygglov', departmentId: 'd1', departmentName: 'Stadsbyggnad', classification: 'internal', publishedAt: null,
    sharedWith: 'department', shared: false, contactName: null, contactEmail: null, documentCount: 0, topics: [], lastRun: null,
    latestEvaluation: null, ...overrides,
  }
}

/** A list row of `GET /api/assistants`: published (version 1), no draft. */
export function assistantSummary(overrides: Partial<AssistantSummary> = {}): AssistantSummary {
  return {
    id: 'a1', slug: 'bygglov', name: 'Bygglovsassistenten', departmentId: 'd1', classification: 'internal', state: 'active', published: true, hasDraft: false,
    generalKnowledge: null, collections: [], model: null, publishedVersion: 1, publishedAt: '2026-11-01T09:00:00Z', questions7d: 0, ...overrides,
  }
}

/** An assistant in a knowledge base's `usedBy`. */
export function collectionUser(overrides: Partial<CollectionUser> = {}): CollectionUser {
  return {
    id: 'a1', name: 'Bygglovsassistenten', classification: 'internal', state: 'active', published: true, draft: false, departmentId: 'd1', departmentName: 'Stadsbyggnad', topics: [],
    ...overrides,
  }
}

/** rag-admin's sign-in values of `GET /bff/setup/state`, all defaults (a first run); override per value. */
export function signInSettings(overrides: Partial<SignInSettings> = {}): SignInSettings {
  return {
    authority: { value: null, source: 'default' },
    clientId: { value: 'ume-rag-admin', source: 'default' },
    clientSecret: { set: false, source: 'default', setAt: null, expiresOn: null, unreadable: false },
    roleClaim: { value: 'roles', source: 'default' },
    departmentClaim: { value: 'departmentCodes', source: 'default' },
    teamClaim: { value: 'teamCodes', source: 'default' },
    groupClaim: { value: 'groups', source: 'default' },
    extraScopes: { value: [], source: 'default' },
    tenantId: { value: null, source: 'default' },
    refuseGuests: { value: true, source: 'default' },
    version: '1',
    ...overrides,
  }
}

/** The wizard's values with nothing set; override what the test types. */
export function wizardValues(overrides: Partial<OidcWizardValues> = {}): OidcWizardValues {
  return {
    authority: null, clientId: null, roleClaim: null, departmentClaim: null, teamClaim: null, groupClaim: null, extraScopes: null, tenantId: null,
    refuseGuests: null, clientSecretExpiresOn: null, ...overrides,
  }
}

/** A passed test by a platform admin of the provider https://idp.example.org/realms/ume. */
export function setupTest(overrides: Partial<SetupTest> = {}): SetupTest {
  return {
    testId: 't1', target: 'rag-admin', status: 'passed', reason: null, startedAt: '2026-11-15T10:00:00Z', expiresAt: '2026-11-15T10:10:00Z',
    values: wizardValues({ authority: 'https://idp.example.org/realms/ume' }), clientSecret: 'typed',
    result: {
      subject: 'sub-1', name: 'Anna Andersson', roles: ['rag-platform-admin'], groups: ['rag-admins'], groupCount: 1, departmentCodes: ['1000'], teamCodes: [],
      isPlatformAdmin: true, claimsRead: { role: 'roles', group: 'groups', department: 'departmentCodes', team: 'teamCodes', tenantId: null, refuseGuests: true },
    },
    ...overrides,
  }
}

/** Open WebUI's sign-in values of a first run with the sign-in provider https://idp.example.org/realms/ume: all defaults. */
export function openWebUiSignIn(overrides: Partial<OpenWebUiSignIn> = {}): OpenWebUiSignIn {
  return {
    providerUrl: { value: 'https://idp.example.org/realms/ume/.well-known/openid-configuration', source: 'default' },
    clientId: { value: 'open-webui', source: 'default' },
    providerName: { value: 'Umeå kommun', source: 'default' },
    clientSecret: { set: false, source: 'default', setAt: null, expiresOn: null, unreadable: false },
    configured: true,
    ...overrides,
  }
}

/** Open WebUI's values with nothing set; override what the check typed. */
export function openWebUiValues(overrides: Partial<OpenWebUiWizardValues> = {}): OpenWebUiWizardValues {
  return { providerUrl: null, clientId: null, providerName: null, clientSecretExpiresOn: null, ...overrides }
}

/** A passed Open WebUI check whose client the provider accepted, with a typed secret and nothing else typed. */
export function openWebUiTest(overrides: Partial<OpenWebUiTest> = {}): OpenWebUiTest {
  return {
    testId: 'w1', target: 'open-webui', status: 'passed', reason: null, startedAt: '2026-11-15T10:05:00Z', values: openWebUiValues(), clientSecret: 'typed',
    result: { discovery: 'ok', clientAccepted: true, clientReason: null },
    ...overrides,
  }
}

/** Open WebUI's block of `GET /bff/setup/state`: by default the deployment does not run it. */
export function openWebUiState(overrides: Partial<SetupOpenWebUiState> = {}): SetupOpenWebUiState {
  return { enabled: false, signIn: openWebUiSignIn(), redirectUri: 'https://chat.example.org/oauth/oidc/login/callback', latestTest: null, ...overrides }
}

/** `GET /bff/setup/state` of a first run: the window ends in 50 minutes, the session in 25; the gateway lacks the feature `oidc-authority`. */
export function setupState(overrides: Partial<SetupState> = {}, now: number = Date.now()): SetupState {
  return {
    kind: 'first_run',
    endsAt: new Date(now + 50 * 60_000).toISOString(),
    sessionExpiresAt: new Date(now + 25 * 60_000).toISOString(),
    signIn: signInSettings(),
    redirectUris: { signIn: 'https://rag-admin.example.org/signin-oidc', signOutCallback: 'https://rag-admin.example.org/signout-callback-oidc' },
    deployment: { gatewayBaseUrl: 'https://gateway.example.org', apiPublicUrl: null, platformAdminGroups: [] },
    gateway: null,
    latestTest: null,
    openWebUi: openWebUiState(),
    ...overrides,
  }
}

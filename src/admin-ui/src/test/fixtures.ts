import type { BffUser, Collection, CollectionSummary, CollectionUser, Department } from '@/api/types'

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
    sharedWith: 'department', shared: false, contactName: null, contactEmail: null, documentCount: 0, topics: [], ...overrides,
  }
}

/** An assistant in a knowledge base's `usedBy`. */
export function collectionUser(overrides: Partial<CollectionUser> = {}): CollectionUser {
  return {
    id: 'a1', name: 'Bygglovsassistenten', classification: 'internal', state: 'active', published: true, draft: false, departmentId: 'd1', departmentName: 'Stadsbyggnad', topics: [],
    ...overrides,
  }
}

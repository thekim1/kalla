import type { BffUser, Department } from '@/api/types'

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

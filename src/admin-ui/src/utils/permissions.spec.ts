import { describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import raw from '../../../../contracts/admin/v1/permissions.json?raw'
import { ROLES } from '@/api/types'
import { MANAGE_ROLES, PLATFORM_ADMIN } from '@/router/nav'
import { useAuthStore } from '@/stores/auth'
import { POLICIES, rolesFor, type Policy } from './permissions'

// The admin API's permission matrix as its tests export it (admin design §3.1); read here as text, not through the module.
const contract = JSON.parse(raw) as { policies: Record<string, string[]> }
const contractRoles = [...new Set(Object.values(contract.policies).flat())].sort()

function signIn(roles: string[]) {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = { isAuthenticated: true, name: 'A', email: null, roles, departmentCodes: [], departmentSources: [], sessionExpiresAt: null, signIn: { configured: true }, setup: { open: false }, openWebUi: { enabled: true } }
  auth.status = 'authenticated'
  return auth
}

describe('permissions (contracts/admin/v1/permissions.json)', () => {
  it('knows every role and policy of the contract', () => {
    expect(contractRoles).toEqual([...ROLES].sort())
    expect([...POLICIES].sort()).toEqual(Object.keys(contract.policies).sort())
  })

  it('answers can() for every role and policy as the contract says', () => {
    for (const role of contractRoles) {
      const auth = signIn([role])
      for (const [policy, roles] of Object.entries(contract.policies)) {
        expect(auth.can(policy as Policy), `${role} ${policy}`).toBe(roles.includes(role))
      }
    }
  })

  it('grants nothing without a rag-* role or a session', () => {
    const auth = signIn(['some-other-app-role'])
    for (const policy of POLICIES) expect(auth.can(policy), policy).toBe(false)
    auth.status = 'unauthenticated'
    auth.user = { ...auth.user!, roles: [...ROLES] }
    for (const policy of POLICIES) expect(auth.can(policy), policy).toBe(false)
  })

  it('gives the menu its role lists from the matrix', () => {
    expect([...PLATFORM_ADMIN].sort()).toEqual(contract.policies.admin)
    expect([...MANAGE_ROLES].sort()).toEqual(contract.policies.manage)
    expect(rolesFor('read').sort()).toEqual([...ROLES].sort())
  })
})

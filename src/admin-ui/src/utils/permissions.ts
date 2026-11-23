import contract from '../../../../contracts/admin/v1/permissions.json'
import { ROLES, type Role } from '@/api/types'

/**
 * The admin API's permission matrix (admin design §3.1) as `contracts/admin/v1/permissions.json` exports it from
 * `PermissionMatrix`: each policy and the roles that hold it, a combined policy's roles resolved. The API's tests rewrite
 * the file, so the UI follows the matrix without a copy of its own. It only hides what the API would refuse.
 */
export type Policy = keyof typeof contract.policies

export const POLICIES = Object.keys(contract.policies) as Policy[]

function isRole(role: string): role is Role {
  return (ROLES as readonly string[]).includes(role)
}

/** The roles that hold `policy`. */
export function rolesFor(policy: Policy): Role[] {
  return contract.policies[policy].filter(isRole)
}

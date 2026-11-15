/**
 * A link into the gateway's admin (admin design §2.3): `path` appended to the gateway's `admin_url`, or null when the address
 * is missing or not an http(s) URL. The result becomes a link's `href`, so an address such as `javascript:` from a
 * misconfigured or compromised gateway context never does (#712).
 */
export function gatewayHref(adminUrl: string | null | undefined, path: string): string | null {
  return adminUrl && /^https?:\/\//i.test(adminUrl) ? `${adminUrl.replace(/\/+$/, '')}${path}` : null
}

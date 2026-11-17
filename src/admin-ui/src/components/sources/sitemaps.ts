/** The crawl setting `sitemaps` (#801): at most 20 absolute http(s) addresses, as the API's schema allows. */
export const MAX_SITEMAPS = 20

export function sitemapsValid(entries: string[]): boolean {
  return entries.length <= MAX_SITEMAPS && entries.every((u) => /^https?:\/\//.test(u) && u.length <= 2048)
}

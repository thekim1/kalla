/**
 * Admin forms hold configuration, not personal or login data. Browsers honour autocomplete="off", but password
 * managers do not: each has its own opt-out attribute (1Password, LastPass, Bitwarden, Dashlane).
 */
export const NO_PASSWORD_MANAGER = {
  'data-1p-ignore': '',
  'data-lpignore': 'true',
  'data-bwignore': '',
  'data-form-type': 'other',
} as const

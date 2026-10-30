import type { DepartmentSource, Role } from '@/api/types'

/**
 * Every Swedish word of the admin UI except error messages (those are in messages.ts). Admin design §5.7: one glossary,
 * klarspråk, du-tilltal, sentence case, verbs on buttons. Components never hold their own UI text.
 */

export type Tone = 'ok' | 'warn' | 'danger' | 'neutral' | 'accent'

/** Copied from the gateway. */
export const TONE_CLASSES: Record<Tone, string> = {
  ok: 'bg-ok-soft text-ok',
  warn: 'bg-warn-soft text-warn',
  danger: 'bg-danger-soft text-danger',
  neutral: 'bg-sunken text-fg-2',
  accent: 'bg-accent-soft text-accent-ink',
}

/** Product name N1 is open (admin design §5.1); "Kunskapsplattform" until it is decided. */
export const APP = {
  name: 'Kunskapsplattform',
  organisation: 'Umeå kommun',
  titleSuffix: 'Kunskapsplattform',
  loading: 'Laddar …',
} as const

export const ROLE_LABEL: Record<Role, string> = {
  'rag-platform-admin': 'Plattformsadministratör',
  'rag-department-admin': 'Förvaltningsadministratör',
  'rag-pipeline-editor': 'Bearbetningsredaktör',
  'rag-reviewer': 'Granskare',
  'rag-skill-editor': 'Färdighetsredaktör',
  'rag-editor': 'Redaktör',
  'rag-viewer': 'Läsbehörighet',
}

export const DEPARTMENT_SOURCE_LABEL: Record<DepartmentSource, string> = {
  claim: 'Från ditt konto',
  group: 'Via medlemsgrupp',
}

export const COMMON = {
  loading: 'Laddar',
  retry: 'Försök igen',
  empty: 'Inget att visa ännu.',
  clearFilters: 'Rensa filter',
  cancel: 'Avbryt',
  close: 'Stäng',
  previous: 'Föregående',
  next: 'Nästa',
  yes: 'Ja',
  no: 'Nej',
  active: 'Aktiv',
  inactive: 'Inaktiv',
} as const

export const PAGINATION = {
  range: (first: string, last: string, total: string) => `${first}–${last} av ${total}`,
  page: (page: number, count: number) => `Sida ${page} av ${count}`,
} as const

export const RELATIVE = {
  justNow: 'Nyss',
  minutesAgo: (n: number) => `för ${n} min sedan`,
  hoursAgo: (n: number) => `för ${n} h sedan`,
  yesterday: 'I går',
  daysAgo: (n: number) => `för ${n} dagar sedan`,
} as const

export const THEME_LABEL = {
  light: 'Ljust',
  dark: 'Mörkt',
  lumen: 'Lumen',
} as const

export const SHELL = {
  skipToMain: 'Hoppa till huvudinnehållet',
  mainNav: 'Huvudmeny',
  quickAccess: 'Snabbval',
  needAttention: ' behöver åtgärdas',
  theme: 'Tema',
  signOut: 'Logga ut',
  search: 'Sök sidor och inställningar',
  searchShortcut: 'Ctrl K',
  development: 'Utveckling',
  production: 'Produktion',
  menu: 'Meny',
  menuDescription: 'Alla sidor, tema och ditt konto.',
  closeMenu: 'Stäng menyn',
  session: 'Session',
  sessionLeft: (minutes: number) => (minutes >= 60 ? `${Math.floor(minutes / 60)} h ${minutes % 60} min kvar` : `${minutes} min kvar`),
} as const

export const PALETTE = {
  title: 'Sök sidor och inställningar',
  help: 'Skriv för att filtrera, välj med piltangenterna och öppna med Enter.',
  list: 'Sidor och inställningar',
  escape: 'Esc',
  noMatch: (query: string) => `Inget matchar ”${query}”.`,
} as const

export const SIGN_IN = {
  title: 'Logga in på Kunskapsplattformen',
  lead: 'Administration för Umeå kommuns kunskapsbanker och assistenter. Logga in med ditt organisationskonto.',
  failed: 'Det gick inte att kontrollera din inloggning. Försök igen.',
  expired: 'Din session har gått ut. Logga in igen.',
  button: 'Logga in',
  /** Identity design §1.7: why a sign-in was refused (`/?signin=refused&reason=…`). */
  refused: {
    wrong_tenant: 'Ditt konto hör inte till Umeå kommuns organisation. Logga in med ditt organisationskonto.',
    guest_refused: 'Gästkonton kan inte logga in här. Logga in med ditt eget konto i Umeå kommun.',
  } as Record<string, string>,
  refusedFallback: 'Inloggningen nekades. Kontakta plattformsteamet om du behöver åtkomst.',
} as const

export const SESSION = {
  expiringTitle: 'Din session går snart ut',
  expiringBody: 'Stanna inloggad så behåller du det du inte har sparat.',
  atLimitBody: 'En session får vara högst en viss tid. Spara ditt arbete och logga sedan in igen.',
  stay: 'Stanna inloggad',
} as const

export const NAV_GROUP = {
  overview: 'Överblick',
  platform: 'Plattform',
} as const

export const PAGE = {
  overview: { title: 'Översikt', description: 'Din behörighet och det som behöver göras' },
  organisation: { title: 'Organisation', description: 'Förvaltningar och team från AI-gatewayen' },
  forbidden: { title: 'Ingen åtkomst' },
  notFound: { title: 'Sidan finns inte' },
} as const

export const FORBIDDEN = {
  /** Admin design §5.6, "No role". */
  body: 'Din roll ger inte åtkomst till den här sidan. Kontakta plattformsteamet om du behöver den.',
  home: 'Till översikten',
} as const

export const NOT_FOUND = {
  body: 'Sidan du letar efter finns inte. Kontrollera adressen eller gå till översikten.',
  home: 'Till översikten',
} as const

export const OVERVIEW = {
  welcome: (name: string) => `Välkommen, ${name}`,
  lead: 'Här bygger du kunskapsbanker och assistenter för din förvaltning. Sidorna för det kommer i nästa version.',
  access: 'Din behörighet',
  roles: 'Roller',
  departments: 'Förvaltningar',
  source: 'Varifrån',
  noRoles: 'Du har ingen roll i Kunskapsplattformen än. Kontakta plattformsteamet om du behöver åtkomst.',
  noDepartments: 'Inga förvaltningar är kopplade till ditt konto.',
  allDepartments: 'Som plattformsadministratör ser du alla förvaltningar.',
} as const

export const ORGANISATION = {
  lead: 'Förvaltningar och team hämtas från AI-gatewayen. Ändra dem i gatewayens administration.',
  departments: 'Förvaltningar',
  name: 'Namn',
  code: 'Kod',
  teams: 'Team',
  status: 'Status',
  syncedAt: 'Hämtad',
  noTeams: 'Inga team',
  empty: 'Inga förvaltningar har hämtats än. Synkningen körs var femte minut.',
  syncNow: 'Hämta nu',
  synced: (departments: number, teams: number) =>
    departments + teams === 0 ? 'Allt var redan uppdaterat.' : `Uppdaterade ${departments} förvaltningar och ${teams} team.`,
  lastRun: (when: string) => `Senast hämtad ${when}`,
  /** Admin design §4.10: shown to platform admins after three failed runs. */
  failing: (since: string) => `Synkningen mot AI-gatewayen fungerar inte sedan ${since}.`,
} as const

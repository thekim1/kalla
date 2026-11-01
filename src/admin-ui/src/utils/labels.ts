import type { DepartmentSource, Level, Role, SourceType } from '@/api/types'

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
  content: 'Innehåll',
  platform: 'Plattform',
} as const

export const PAGE = {
  overview: { title: 'Översikt', description: 'Din behörighet och det som behöver göras' },
  organisation: { title: 'Organisation', description: 'Förvaltningar och team från AI-gatewayen' },
  collections: { title: 'Kunskapsbanker', description: 'Innehållet som assistenterna söker i' },
  platform: { title: 'Plattformsobjekt', description: 'Klienter, nycklar, inställningar, dataskydd, webbplatsprofiler och modeller' },
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

/** Informationsklass (admin design §5.7 glossary). */
export const LEVEL_LABEL: Record<Level, string> = { public: 'Offentlig', internal: 'Intern', sensitive: 'Känslig' }

/** "Vem får läsa innehållet?" (admin design §5.4 step 2): the three levels explained, none preselected. */
export const LEVEL_HELP: Record<Level, string> = {
  public: 'Alla får läsa det, också invånare. Till exempel sidor på umea.se.',
  internal: 'Bara medarbetare i kommunen får läsa det.',
  sensitive: 'Bara de som behöver det i sitt arbete får läsa det. Kräver extra skydd.',
}

export const SOURCE_TYPE_LABEL: Record<SourceType, string> = { 'url-list': 'Adresslista', crawl: 'Webbplats', upload: 'Filer', sharepoint: 'SharePoint' }

export const COLLECTIONS = {
  lead: 'En kunskapsbank samlar källor, till exempel en webbplats eller filer, som assistenterna kan söka i.',
  empty: 'Här bygger du kunskapsbanker och assistenter för din förvaltning.',
  create: 'Skapa kunskapsbank',
  search: 'Sök kunskapsbank',
  name: 'Namn',
  level: 'Informationsklass',
  levelQuestion: 'Vem får läsa innehållet?',
  levelRequired: 'Välj vem som får läsa innehållet.',
  nameRequired: 'Skriv ett namn.',
  department: 'Förvaltning',
  departmentRequired: 'Välj förvaltning.',
  ownerTeam: 'Ansvarigt team',
  noTeam: 'Inget team',
  state: 'Läge',
  published: 'Publicerad',
  notPublished: 'Ej publicerad',
  notPublishedHint: 'Ej publicerad: syns bara i Testa.',
  created: (name: string) => `Kunskapsbanken ${name} är skapad.`,
  list: 'Kunskapsbanker',
} as const

export const COLLECTION = {
  tabs: { overview: 'Översikt', sources: 'Källor', documents: 'Dokument' },
  tabsLabel: 'Delar av kunskapsbanken',
  usedBy: 'Används av',
  notUsed: 'Ingen assistent använder kunskapsbanken än.',
  draftOnly: 'utkast',
  publish: 'Publicera',
  published: (name: string) => `${name} är publicerad.`,
  sources: 'Källor',
  noSources: 'Lägg till en källa, till exempel en webbplats eller filer.',
  sourceName: 'Namn',
  sourceType: 'Typ',
  sourceState: 'Läge',
  sourceStates: { active: 'Aktiv', paused: 'Pausad' } as Record<string, string>,
  documents: 'Dokument',
  noDocuments: 'Inga dokument ännu. De kommer när en källa har hämtats.',
  documentTitle: 'Dokument',
  documentFetched: 'Hämtat',
  documentState: 'Läge',
  onlyPii: 'Bara dokument med personuppgifter',
  pii: 'Personuppgifter',
  piiCleared: 'Godkänt för offentlig',
  held: 'Behållet',
  live: 'Sökbart',
  notLive: 'Inte sökbart',
  more: 'Visa fler',
} as const

/** The generic forms for platform objects (admin design §5.8). Field labels come from the API's schemas. */
export const PLATFORM = {
  lead: 'Det plattformsteamet ställer in för hela plattformen. Fälten kommer från API:ets scheman.',
  resources: {
    clients: { label: 'Klienter', description: 'Appar som använder plattformen: Open WebUI, integrationer, interna klienter' },
    keys: { label: 'Nycklar', description: 'API-nycklar per klient; skapa, rotera, visa och återkalla' },
    settings: { label: 'Inställningar', description: 'Plattformens inställningar per avsnitt' },
    dataProtection: { label: 'Dataskydd', description: 'Historik, lagringstid och kryptering per informationsklass' },
    siteProfiles: { label: 'Webbplatsprofiler', description: 'Hur en webbplats sidor läses: innehåll, brus och identitet' },
    gatewayModels: { label: 'Gateway-modeller', description: 'Modellerna från AI-gatewayens katalog' },
  },
  settingsSections: {
    network: 'Nätverk',
    retention: 'Lagringstider',
    'deletion-guard': 'Raderingsskydd',
    crawl: 'Webbinsamling',
    evaluation: 'Utvärdering',
    retrieval: 'Sökning',
    chat: 'Chatt',
    pii: 'Personuppgifter',
    worker: 'Bearbetning',
  },
  columns: {
    name: 'Namn',
    kind: 'Typ',
    ceiling: 'Högsta klass',
    enabled: 'Aktiv',
    prefix: 'Prefix',
    status: 'Status',
    expiresAt: 'Går ut',
    section: 'Avsnitt',
    level: 'Informationsklass',
    historyMode: 'Historik',
    retentionDays: 'Lagringstid (dagar)',
    encryption: 'Kryptering',
    host: 'Värd',
    preset: 'Förinställning',
    version: 'Version',
    appliedAt: 'Gäller sedan',
    residencies: 'Hemvist',
    source: 'Källa',
  },
  actions: {
    rotate: 'Rotera',
    reveal: 'Visa nyckel',
    revoke: 'Återkalla',
    revokeConfirm: 'Nyckeln slutar fungera direkt. Anrop med den nekas.',
    rollback: 'Återställ till version',
  },
  form: {
    create: 'Skapa',
    save: 'Spara',
    createTitle: (what: string) => `Ny: ${what.toLowerCase()}`,
    saved: 'Sparat',
    none: '(ingen)',
    required: 'Fyll i fältet.',
    invalidJson: 'Det här är inte giltig JSON.',
    invalidNumber: 'Skriv ett tal.',
    listHint: 'Ett värde per rad.',
    jsonHint: 'JSON; servern kontrollerar innehållet.',
    secretOnce: 'Nyckeln visas bara nu. Kopiera den och spara den säkert.',
    copy: 'Kopiera',
  },
} as const

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
  assistants: { title: 'Assistenter', description: 'Assistenterna som svarar i Open WebUI och andra klienter' },
  source: { title: 'Källa' },
  run: { title: 'Körning' },
  publication: { title: 'Publicering' },
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

export const SOURCE = {
  add: 'Lägg till källa',
  chooseType: 'Vad vill du hämta?',
  typeHelp: {
    'url-list': 'Bestämda sidor. Bara adresserna i listan hämtas, inga länkar följs.',
    crawl: 'En hel webbplats eller en del av den. Länkar följs från startadresserna.',
    upload: 'Filer som du laddar upp, till exempel PDF eller Word.',
    sharepoint: 'Dokument i SharePoint.',
  } as Record<SourceType, string>,
  uploadLater: 'Filer laddar du upp på källans sida när den är skapad.',
  notYet: 'Kommer senare.',
  name: 'Namn på källan',
  nameRequired: 'Skriv ett namn på källan.',
  level: 'Informationsklass',
  levelHint: 'Högst samma som kunskapsbanken.',
  urls: 'Adresser',
  urlsHint: 'En adress per rad, som börjar med https://.',
  urlsRequired: 'Skriv minst en adress som börjar med http:// eller https://.',
  startUrls: 'Startadresser',
  startUrlsHint: 'En per rad. Webbplatsen hämtas från dem och följer länkar inom samma webbplats.',
  advanced: 'Fler inställningar',
  include: 'Ta bara med',
  includeHint: 'Mönster, ett per rad, till exempel www.umea.se/bygga/**. Tomt betyder hela webbplatsen.',
  exclude: 'Hoppa över',
  excludeHint: 'Mönster, ett per rad. Hoppa över vinner över Ta bara med.',
  maxPages: 'Högst antal sidor',
  maxDepth: 'Högst antal länksteg från en startadress',
  numberInvalid: 'Skriv ett heltal.',
  piiMode: 'Om ett dokument kan innehålla personuppgifter',
  piiModes: {
    hold: 'Behåll det utanför sökningen tills någon har tittat på det',
    report: 'Gör det sökbart och visa det i rapporten (personnummer hålls alltid utanför)',
  } as Record<'hold' | 'report', string>,
  check: 'Kontrollera',
  checking: 'Kontrollerar webbplatsen',
  checkResult: 'Resultat av kontrollen',
  checkStates: { pass: 'Klart', warn: 'Obs', fail: 'Stoppar' } as Record<'pass' | 'warn' | 'fail', string>,
  checkFailed: 'Kontrollen hittade något som måste rättas innan du sparar.',
  estimatedPages: (n: string) => `Ungefär ${n} sidor`,
  save: 'Spara och hämta',
  preview: 'Förhandsgranska',
  previewHint: 'Hämtar upp till 5 av adresserna och visar vad som blir kvar av sidorna. Inget sparas.',
  previewFirst: 'Bara de 5 första adresserna förhandsgranskas.',
  previewing: 'Förhandsgranskar sidorna',
  previewResult: 'Resultat av förhandsgranskningen',
  noPipeline: 'Det finns ingen bearbetning för den här källtypen än. Kontakta plattformsteamet.',
  saved: (name: string) => `Källan ${name} är sparad och hämtningen har börjat.`,
  savedNoSync: (name: string) => `Källan ${name} är sparad.`,
} as const

/** The preview result (admin design §6.4): documents as tabs, the stage strip, Före / Efter and each stage's output. */
export const PREVIEW = {
  documents: 'Förhandsgranskade sidor',
  document: (n: number) => `Sida ${n}`,
  stages: 'Steg',
  view: 'Visa',
  beforeAfter: 'Före / Efter',
  before: 'Före: utan webbplatsprofil',
  after: 'Efter: med webbplatsprofil',
  profileManual: (host: string) => `Webbplatsprofil för ${host}: egen profil`,
  profilePreset: (host: string, preset: string) => `Webbplatsprofil för ${host}: förinställningen ${preset}`,
  profileGeneric: (host: string) => `Webbplatsprofil för ${host}: ingen, allmänna regler`,
  stageLabel: {
    fetch: 'Hämta', scan: 'Virussökning', detect: 'Filtyp', parse: 'Läs in', clean: 'Rensa', chunk: 'Textavsnitt',
    'personal-data': 'Personuppgifter', embed: 'Vektorer', index: 'Index',
  } as Record<string, string>,
  stageState: { succeeded: 'Klart', failed: 'Misslyckades', skipped: 'Hoppades över' } as Record<string, string>,
  reused: 'återanvänt',
  breadcrumb: 'Brödsmulor',
  visibleDate: 'Datum på sidan',
  documentDate: 'Dokumentets datum',
  title: 'Rubrik',
  noText: 'Ingen text blev kvar.',
  chunks: (n: number, tokens: number) => `${n.toLocaleString('sv-SE')} textavsnitt, ${tokens.toLocaleString('sv-SE')} token`,
  tokens: (n: number) => `${n.toLocaleString('sv-SE')} token`,
  page: (n: number) => `sida ${n}`,
  piiState: 'Läge i kunskapsbanken',
  piiStates: { live: 'Sökbart', held: 'Hålls utanför sökningen' } as Record<string, string>,
  piiCounts: 'Hittade personuppgifter',
  piiTypes: {
    personnummer: 'Personnummer', samordningsnummer: 'Samordningsnummer', email: 'E-postadresser', emailExternal: 'Externa e-postadresser',
    phone: 'Telefonnummer', name: 'Namn',
  } as Record<string, string>,
  piiFired: 'Gör att dokumentet flaggas',
  piiNone: 'Inga personuppgifter hittades.',
  truncated: 'Utdata är större än 2 MB och visas avkortad.',
  noOutput: 'Steget har ingen utdata att visa.',
  expired: 'Förhandsgranskningen finns inte längre (den sparas i 24 timmar). Kör den igen.',
  expires: (time: string) => `Sparas till ${time}.`,
} as const

/** Ändra källa (admin design §5.5.3, V25). */
export const SOURCE_EDIT = {
  title: 'Ändra källa',
  edit: 'Ändra',
  save: 'Spara',
  saved: 'Källan är sparad. Ändringen gäller från nästa hämtning.',
  demote: 'Nedprioritera',
  demoteHint: 'Ett mönster per rad, till exempel www.umea.se/bygga/**/arkiv/**, och efter ett mellanslag en faktor mellan 0 och 1 (utan faktor: 0,5). Träffar från sidorna rankas lägre.',
  demoteInvalid: 'Skriv ett mönster per rad, eventuellt följt av en faktor mellan 0 och 1, till exempel 0,5.',
  impactTitle: 'Ändringen påverkar vad som hämtas',
  scope: (n: number) => `Avgränsningen ändras. Av källans ${n.toLocaleString('sv-SE')} sökbara dokument tas de som hamnar utanför bort vid nästa hämtning.`,
  identity: 'Adresserna känns igen på ett nytt sätt. Dokument kan hämtas på nytt och gamla tas bort.',
  usedBy: (names: string) => `Påverkar assistenterna: ${names}.`,
  draft: 'utkast',
  saveAnyway: 'Spara ändå',
  demoted: 'Nedprioriteras',
} as const

const ratio = (v: number | null) => (v == null ? '–' : v.toLocaleString('sv-SE', { minimumFractionDigits: 2, maximumFractionDigits: 2 }))

/** Publicering (admin design §6.6, §6.7): plan, re-processing, report, Behåll / Återställ. */
export const PUBLICATION = {
  title: 'Publicering',
  lead: (pipeline: string, draft: number) => `Bearbetningen ${pipeline}, version ${draft}`,
  published: 'Publicerad',
  rolledBack: 'Återställd',
  superseded: 'Ersatt av en nyare plan',
  supersededHint: 'En nyare plan finns för samma bearbetning. Öppna den i stället.',
  plan: 'Plan',
  planning: 'Räknar ut vad som behöver bearbetas om',
  planSummary: (documents: string, sources: number) => `${documents} dokument i ${sources} ${sources === 1 ? 'källa' : 'källor'} bearbetas om.`,
  planNote: 'Sökningen använder de gamla textavsnitten tills varje dokument är klart. Steg som inte ändrats återanvänds utan kostnad.',
  estimatedTime: 'Uppskattad tid',
  minutes: (n: number) => (n < 60 ? `cirka ${n} min` : `cirka ${Math.round(n / 6) / 10} h`.replace('.', ',')),
  estimatedCost: 'Uppskattad kostnad',
  parserPages: 'Sidor som tolkas',
  affected: 'Påverkade källor',
  documents: (n: string) => `${n} dokument`,
  waiting: 'Väntar',
  openRun: 'Visa körning',
  confirm: 'Bekräfta',
  confirmTitle: 'Publicera ändringen?',
  confirmConsequence: 'Källorna bearbetas om med de nya inställningarna. Efteråt körs testfrågorna och resultatet visas här.',
  publishedDone: 'Ändringen är publicerad. Källorna bearbetas om.',
  report: 'Resultat',
  reprocessing: (since: string) => `Källorna bearbetas om sedan ${since}. Resultatet visas när de är klara.`,
  evaluating: 'Testfrågorna körs.',
  accuracy: (recall: number | null, questions: number) => `Träffsäkerhet ${ratio(recall)} · ${questions} frågor`,
  compared: (before: number | null, wins: number, losses: number) => `Före: ${ratio(before)} · ${wins} bättre, ${losses} sämre`,
  keep: 'Behåll',
  keptHint: 'Ändringen behålls.',
  rollback: 'Återställ',
  rollbackTitle: (n: number) => `Återställ version ${n}?`,
  rollbackConsequence: (n: number) =>
    `Version ${n} publiceras igen och källorna bearbetas om. Inom 14 dagar tas de tidigare textavsnitten tillbaka utan ny tolkning.`,
  rolledBackDone: (n: number) => `Version ${n} är publicerad igen.`,
  rolledBackHint: 'Ändringen är återställd. En nyare version gäller nu.',
} as const

/** The source page's Bearbetning card and the parameter drawer (admin design §6.3, §6.6). */
export const PIPELINE = {
  title: 'Bearbetning',
  name: 'Bearbetning',
  version: 'Publicerad version',
  draft: (n: number) => `Utkast: version ${n}`,
  edit: 'Ändra bearbetning',
  editLead: 'Ändringen gäller alla källor som använder bearbetningen. Du ser planen innan något bearbetas om.',
  publish: 'Publicera…',
  platformShared: 'Bearbetningen delas av hela plattformen. När du publicerar skapas en egen kopia för kunskapsbanken.',
  params: {
    chunkTokens: 'Textavsnittens längd (token)',
    overlapPercent: 'Överlapp mellan textavsnitt (procent)',
    ocr: 'Textigenkänning i skannade dokument',
    tables: 'Tabeller',
    pathRules: 'Regler per sökväg',
  } as Record<string, string>,
  options: {
    auto: 'Automatiskt', force: 'Alltid', off: 'Av', fast: 'Snabbt', accurate: 'Noggrant',
  } as Record<string, string>,
  range: (min: number, max: number) => `Från ${min} till ${max}.`,
  json: 'JSON. Lämna som det är om du är osäker.',
  invalidNumber: 'Skriv ett heltal.',
  invalidJson: 'Det är inte giltig JSON.',
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

export const SOURCE_PAGE = {
  settings: 'Inställningar',
  addresses: 'Adresser',
  startAddresses: 'Startadresser',
  maxPages: 'Högst antal sidor',
  syncNow: 'Hämta nu',
  syncStarted: 'Hämtningen har börjat.',
  alreadyRunning: 'En hämtning pågår redan.',
  runs: 'Körningar',
  noRuns: 'Ingen körning ännu. Välj Hämta nu.',
  started: 'Startad',
  run: 'Körning',
  state: 'Läge',
  counts: 'Resultat',
  more: 'Visa fler',
  pii: 'Personuppgifter',
  piiNone: 'Inga dokument med personuppgifter.',
  piiHeld: 'Hålls utanför sökningen',
  piiLive: 'Sökbart',
  piiNotApplies: 'Gäller bara offentliga kunskapsbanker.',
} as const

export const RUN_STATE_LABEL: Record<string, string> = {
  running: 'Pågår',
  succeeded: 'Klar',
  degraded: 'Klar med anmärkning',
  failed: 'Misslyckades',
  'awaiting-deletion-confirmation': 'Väntar på beslut om borttagning',
  cancelled: 'Avbruten',
}

export const RUN_PAGE = {
  title: 'Körning',
  added: 'Nya',
  updated: 'Ändrade',
  unchanged: 'Oförändrade',
  deleted: 'Borttagna',
  failed: 'Misslyckade',
  complete: 'Hela webbplatsen gicks igenom.',
  incomplete: 'Webbplatsen gicks inte igenom helt:',
  seeds: (resolved: number, all: number) => `${resolved} av ${all} startadresser svarade`,
  items: 'Dokument i körningen',
  onlyFailed: 'Bara misslyckade',
  noItems: 'Inga dokument att visa.',
  document: 'Dokument',
  outcome: 'Utfall',
  reason: 'Orsak',
  back: 'Till källan',
  more: 'Visa fler',
} as const

/** Run item outcomes as the worker writes them; an unknown one is shown as written. */
export const RUN_OUTCOME_LABEL: Record<string, string> = {
  added: 'Ny',
  updated: 'Ändrad',
  unchanged: 'Oförändrad',
  deleted: 'Borttagen',
  failed: 'Misslyckades',
  skipped: 'Hoppades över',
}

export const ASSISTANTS = {
  lead: 'En assistent svarar på frågor utifrån en eller flera kunskapsbanker.',
  empty: 'Ingen assistent ännu. Skapa en när du har en kunskapsbank.',
  create: 'Skapa assistent',
  list: 'Assistenter',
  name: 'Namn',
  nameRequired: 'Skriv ett namn.',
  department: 'Förvaltning',
  level: 'Informationsklass',
  levelRequired: 'Välj informationsklass.',
  description: 'Beskrivning',
  descriptionHint: 'Vad assistenten hjälper till med. Visas för användarna.',
  contactName: 'Kontaktperson',
  contactEmail: 'E-post till kontaktperson',
  state: 'Läge',
  published: 'Publicerad',
  draftOnly: 'Bara utkast',
  created: (name: string) => `Assistenten ${name} är skapad.`,
} as const

export const ASSISTANT = {
  tabs: { draft: 'Utkast', test: 'Testa', versions: 'Versioner', audience: 'Målgrupp', storage: 'Lagring' },
  tabsLabel: 'Delar av assistenten',
  modelName: (slug: string) => `Modellnamn i Open WebUI: ${slug}`,
  publishedVersion: (n: number) => `Publicerad: version ${n}`,
  model: 'Språkmodell',
  modelHint: 'Modellerna som AI-gatewayen erbjuder.',
  noModel: 'Välj en modell',
  prompt: 'Instruktioner',
  promptHint: 'Hur assistenten ska svara, till exempel ton och vad den inte ska svara på.',
  collections: 'Kunskapsbanker',
  collectionsHint: 'Kunskapsbanker som inte är publicerade fungerar bara i Testa.',
  saveDraft: 'Spara utkast',
  draftSaved: 'Utkastet är sparat.',
  warnings: 'Det här behöver rättas innan du publicerar',
  publish: 'Publicera',
  published: (n: number) => `Version ${n} är publicerad.`,
  baseChanged: 'Utkastet bygger på en äldre version. Publicera ändå?',
  question: 'Fråga',
  ask: 'Testa',
  askHint: 'Testet använder utkastet och sparas inte.',
  answer: 'Svar',
  raw: 'Svar från modellen före filtrering',
  sources: 'Källor i sökningen',
  cited: 'Citerad',
  notPublished: 'ej publicerad',
  gateway: (model: string, residency: string, ms: string) => `${model} · ${residency} · ${ms} ms`,
  outcome: { answered: 'Besvarad', notfound: 'Hittade inget', unavailable: 'Sökningen var inte tillgänglig' } as Record<string, string>,
  audience: 'Klienter som erbjuder assistenten',
  audienceHint: 'Användarna ser assistenten i de klienter du väljer.',
  saveAudience: 'Spara målgrupp',
  audienceSaved: 'Målgruppen är sparad.',
  noClients: 'Det finns inga klienter än. En plattformsadministratör skapar dem under Plattformsobjekt.',
  archived: 'Arkiverad',
  archive: 'Arkivera',
  archiveTitle: 'Arkivera assistenten?',
  archiveConsequence: 'Assistenten försvinner från alla klienter. Versioner, målgrupp och statistik finns kvar, och du kan ta fram den igen.',
  archivedDone: 'Assistenten är arkiverad.',
  unarchive: 'Ta fram igen',
  unarchivedDone: 'Assistenten erbjuds igen med samma publicerade version.',
  versionsLead: 'Varje publicering blir en ny version. Återställ publicerar en tidigare version igen som en ny version.',
  noVersions: 'Inga versioner ännu.',
  version: 'Version',
  versionState: 'Läge',
  versionStates: { draft: 'Utkast', published: 'Publicerad', superseded: 'Tidigare' } as Record<string, string>,
  versionBy: 'Publicerad av',
  versionWhen: 'När',
  restoredFrom: (n: number) => `återställd från version ${n}`,
  restore: 'Återställ',
  restoreTitle: (n: number) => `Återställ version ${n}?`,
  restoreConsequence: (n: number) => `Version ${n} publiceras igen som en ny version. Ditt utkast finns kvar.`,
  restored: (n: number) => `Version ${n} är publicerad.`,
  storageLead: 'Hur samtal med assistenten sparas. Informationsklassen bestämmer vad som är tillåtet.',
  historyMode: 'Historik',
  historyModes: { none: 'Frågor och svar sparas inte' } as Record<string, string>,
  retentionDays: 'Antal dagar som uppgifter om samtalen sparas',
  retentionHint: (max: number) => `Högst ${max} dagar för informationsklassen.`,
  retentionInvalid: (max: number) => `Skriv ett heltal från 1 till ${max}.`,
  saveStorage: 'Spara lagring',
  storageSaved: 'Lagringen är sparad.',
} as const


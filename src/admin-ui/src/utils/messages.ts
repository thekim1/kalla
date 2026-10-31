/**
 * Swedish texts for the admin API's error codes (admin design §4.2), `{param}` filled from the problem's `params`.
 * Every code in contracts/admin/v1/error-codes.json has an entry; messages.spec.ts checks it.
 */
export const ERROR_MESSAGES: Record<string, string> = {
  invalid_value: 'Värdet är inte giltigt. Kontrollera fältet.',
  platform_rule: 'Ändringen bryter mot en regel i plattformen ({constraint}). Kontrollera de markerade fälten.',
  not_found: 'Det finns inte längre, eller så har du inte åtkomst till det. Ladda om sidan.',
  concurrent_change: 'Någon annan har ändrat detta medan du arbetade. Ladda om sidan; dina ändringar finns kvar i formuläret.',
  already_exists: 'Det finns redan något med samma namn eller nyckel. Välj ett annat.',
  in_use: 'Ändringen krockar med något som hänvisar till det. Ta bort hänvisningen först.',
  config_busy: 'En annan ändring sparas just nu. Försök igen om en stund.',
  sync_off: 'Synkningen mot AI-gatewayen är inte inställd. Kontakta plattformsteamet.',
  sync_failed: 'Synkningen mot AI-gatewayen misslyckades ({error}). Den senast hämtade organisationen finns kvar.',
  model_residency: 'Modellen {model} kan inte användas på nivåns tak {ceiling}.',
  model_not_listed: 'AI-gatewayen erbjuder inte {model} på {ceiling} just nu. Försök igen senare.',
  gateway_unavailable: 'AI-gatewayen gick inte att nå för att kontrollera modellen. Försök igen.',
}

/** When the problem has no code we know: by status. */
/**
 * Swedish texts for the worker's refusal and check codes (`Jobs.LastError`, command and preview results; worker #115,
 * #117, issue #116). Shown on runs, commands and previews; an unknown code falls back to `jobMessage`'s generic text.
 */
export const JOB_MESSAGES: Record<string, string> = {
  'site_profile.unknown': 'Webbplatsprofilen finns inte.',
  'site_profile.host_mismatch': 'Profilen gäller en annan webbplats än källan.',
  'site_profile.version_unknown': 'Profilversionen finns inte.',
  'site_profile.version_conflict': 'Profilen har ändrats sedan du öppnade den. Ladda om och försök igen.',
  'collection.unknown': 'Kunskapsbanken finns inte.',
  'eval_question.unknown': 'Testfrågan finns inte.',
  'eval_question.conflict': 'Testfrågan har ändrats av någon annan. Ladda om och försök igen.',
  'eval_question.retired': 'Testfrågan är borttagen och kan inte ändras.',
  'eval_question.unknown_document': 'Det förväntade dokumentet finns inte i kunskapsbanken.',
  'run.unknown': 'Körningen finns inte.',
  'deletion.not_awaiting': 'Körningen väntar inte på ett beslut om borttagning.',
  'source.unknown': 'Källan finns inte.',
  'document.unknown': 'Dokumentet finns inte.',
  'document.deleted': 'Dokumentet är borttaget.',
  'document.no_content': 'Dokumentet har inget sparat innehåll att bearbeta om.',
  'source.paused': 'Källan är pausad. Återuppta den först.',
  'pipeline.unpublished': 'Källans bearbetning har ingen publicerad version.',
  'reprocess.fetch_unsupported': 'Det här dokumentet kan inte hämtas om; bearbeta om det sparade innehållet i stället.',
  'command.invalid_payload': 'Kommandot var felaktigt och kördes inte. Kontakta plattformsteamet.',
  'preview.limit_documents': 'För många dokument för en förhandsgranskning. Välj färre.',
  'preview.invalid_payload': 'Förhandsgranskningen var felaktigt beställd och kördes inte.',
  'preview.unknown_kind': 'Den här sortens förhandsgranskning finns inte.',
  'preview.unsupported': 'Den här förhandsgranskningen finns inte ännu.',
  'preview.collection_unknown': 'Kunskapsbanken finns inte.',
  'preview.source_unknown': 'Källan finns inte.',
  'preview.document_unknown': 'Dokumentet finns inte.',
  'preview.upload_unknown': 'Uppladdningen finns inte längre.',
  'preview.deadline': 'Förhandsgranskningen tog för lång tid och avbröts. Försök med färre dokument.',
  'preflight.none_reachable': 'Ingen av adresserna svarade. Kontrollera adresserna och att webbplatsen är nåbar.',
  'preflight.deadline': 'Kontrollen tog för lång tid och avbröts. Försök igen.',
  'pages.within_limit': 'Antalet sidor ryms inom gränsen.',
  'crawl.limit_pages': 'Webbplatsen har fler sidor än gränsen; bara de första hämtas.',
  'sitemap.found': 'En webbplatskarta hittades.',
  'sitemap.none': 'Ingen webbplatskarta hittades; sidorna hittas via länkar.',
  'robots.ok': 'Webbplatsens robots.txt tillåter hämtning.',
  'robots.missing': 'Webbplatsen har ingen robots.txt; allt får hämtas.',
  'cms.sitevision': 'Webbplatsen är byggd i Sitevision; den färdiga profilen används.',
  'cms.unknown': 'Publiceringssystemet känns inte igen; en profil behöver göras för hand.',
}

/** The Swedish text of a worker code, or a generic text that still names the code for support. */
export function jobMessage(code: string): string {
  return JOB_MESSAGES[code] ?? `Något gick fel i bearbetningen (${code}). Kontakta plattformsteamet om det upprepas.`
}

export const STATUS_MESSAGES = {
  offline: 'Det går inte att nå servern. Kontrollera anslutningen och försök igen.',
  unknown: 'Något gick fel. Försök igen.',
  401: 'Din session har gått ut. Logga in igen.',
  403: 'Du har inte behörighet att göra det.',
  404: 'Det finns inte längre. Ladda om sidan.',
  409: 'Ändringen krockar med något som redan finns.',
  429: 'För många anrop. Vänta en stund och försök igen.',
  server: 'Servern fick ett problem. Försök igen om en stund.',
  rejected: 'Anropet nekades. Kontrollera det du har fyllt i.',
} as const

/** Fills `{name}` placeholders; a missing parameter leaves its placeholder out. */
export function formatMessage(template: string, params?: Record<string, unknown>): string {
  return template.replace(/\{(\w+)\}/g, (_, name: string) => (params?.[name] === undefined ? '' : String(params[name])))
}

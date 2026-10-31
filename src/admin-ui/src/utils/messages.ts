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

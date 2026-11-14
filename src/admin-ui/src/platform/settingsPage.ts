import { PLATFORM, PROMPT_TEXTS, SETTINGS_PAGE } from '@/utils/labels'
import type { ModelField } from './modelFields'

/**
 * The Settings page's sections (admin design §5.10), in the order of the section list. A `settings` section is one
 * section of `config."Settings"` (`GET`/`PUT /api/settings/{section}`, admin design §4.8); `prompt-texts` is the chat
 * section's prompt panel; a `resource` section is a platform object's list (site profiles, data protection).
 */

/** The sections of `config."Settings"`. */
export const SETTINGS_SECTIONS = ['network', 'retention', 'deletion-guard', 'crawl', 'evaluation', 'retrieval', 'chat', 'pii', 'worker'] as const
export type SettingsSectionName = (typeof SETTINGS_SECTIONS)[number]

/** The settings sections' model fields, as V32 checks them (`rerankModel` is a chat model; `gatewayRerankModel` a rerank model). */
export const SETTINGS_MODEL_FIELDS: Partial<Record<SettingsSectionName, readonly ModelField[]>> = {
  evaluation: [
    { name: 'evaluationModel', modelKind: 'chat' },
    { name: 'evaluationJudgeModel', modelKind: 'chat' },
  ],
  // The platform answer ceiling (Decisions "Answer length from the model", admin design §4.5): a number of its own above the JSON.
  chat: [{ name: 'condenseModel', modelKind: 'chat', in: 'value' }, { name: 'maxAnswerTokens', kind: 'number', in: 'value' }],
  retrieval: [
    { name: 'rerankModel', modelKind: 'chat', in: 'value' },
    { name: 'gatewayRerankModel', modelKind: 'rerank', in: 'value' },
  ],
}

export type SettingsGroup = keyof typeof SETTINGS_PAGE.groups

export type SettingsPageSection =
  | { id: SettingsSectionName; kind: 'settings'; group: SettingsGroup; label: string }
  | { id: 'prompt-texts'; kind: 'prompt-texts'; group: SettingsGroup; label: string }
  | { id: 'site-profiles' | 'data-protection'; kind: 'resource'; group: SettingsGroup; label: string }

const S = PLATFORM.settingsSections

export const SETTINGS_PAGE_SECTIONS: readonly SettingsPageSection[] = [
  { id: 'chat', kind: 'settings', group: 'answers', label: S.chat },
  { id: 'prompt-texts', kind: 'prompt-texts', group: 'answers', label: PROMPT_TEXTS.title },
  { id: 'retrieval', kind: 'settings', group: 'answers', label: S.retrieval },
  { id: 'crawl', kind: 'settings', group: 'content', label: S.crawl },
  { id: 'site-profiles', kind: 'resource', group: 'content', label: PLATFORM.resources.siteProfiles.label },
  { id: 'worker', kind: 'settings', group: 'content', label: S.worker },
  { id: 'pii', kind: 'settings', group: 'content', label: S.pii },
  { id: 'data-protection', kind: 'resource', group: 'protection', label: PLATFORM.resources.dataProtection.label },
  { id: 'retention', kind: 'settings', group: 'protection', label: S.retention },
  { id: 'deletion-guard', kind: 'settings', group: 'protection', label: S['deletion-guard'] },
  { id: 'evaluation', kind: 'settings', group: 'quality', label: S.evaluation },
  { id: 'network', kind: 'settings', group: 'platform', label: S.network },
]

/** The section list, grouped, in order. */
export function settingsGroups(): { id: SettingsGroup; label: string; sections: SettingsPageSection[] }[] {
  return (Object.keys(SETTINGS_PAGE.groups) as SettingsGroup[]).map((id) => ({
    id, label: SETTINGS_PAGE.groups[id], sections: SETTINGS_PAGE_SECTIONS.filter((s) => s.group === id),
  }))
}

export function settingsSection(id: string | undefined): SettingsPageSection | undefined {
  return SETTINGS_PAGE_SECTIONS.find((s) => s.id === id)
}

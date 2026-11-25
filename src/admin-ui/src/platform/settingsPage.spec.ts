import { describe, expect, it } from 'vitest'
import { SETTINGS_PAGE } from '@/utils/labels'
import { settingsGroups } from './settingsPage'

describe('Settings page sections (admin design §5.10)', () => {
  it('groups the sections by what they affect, in order', () => {
    expect(settingsGroups().map((g) => [g.label, g.sections.map((s) => s.id)])).toEqual([
      [SETTINGS_PAGE.groups.answers, ['chat', 'prompt-texts', 'retrieval', 'assistant-templates']],
      [SETTINGS_PAGE.groups.content, ['crawl', 'site-profiles', 'worker', 'evaluation']],
      [SETTINGS_PAGE.groups.protection, ['data-protection', 'pii', 'retention', 'deletion-guard']],
      [SETTINGS_PAGE.groups.platform, ['network', 'identity']],
    ])
    expect(SETTINGS_PAGE.groups.answers).toBe('Answers')
  })
})

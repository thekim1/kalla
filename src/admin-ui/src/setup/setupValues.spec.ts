import { describe, expect, it } from 'vitest'
import { setupTest, signInSettings, wizardValues } from '@/test/fixtures'
import {
  applyPreset, canFinish, completeRequestOf, isStale, newForm, presetOf, secretOf, testRequestOf, valuesOf,
} from './setupValues'

const AUTHORITY = 'https://idp.example.org/realms/ume'

describe('setup values (admin design §5.11, identity design §19)', () => {
  it('sends only what is typed: every field left at its default is null', () => {
    const signIn = signInSettings()
    const form = newForm(signIn)
    expect(form.clientId).toBe('')
    expect(form.refuseGuests).toBe(true)
    form.authority = `  ${AUTHORITY} `
    expect(testRequestOf(form, signIn)).toEqual({ ...wizardValues({ authority: AUTHORITY }), clientSecret: null })
  })

  it('sends a typed value, scopes as a list without repeats, and refusing guests off as false', () => {
    const signIn = signInSettings()
    const form = newForm(signIn)
    Object.assign(form, { authority: AUTHORITY, roleClaim: 'app_roles', extraScopes: 'offline_access, groups groups', refuseGuests: false, clientSecretExpiresOn: '2027-06-30' })
    expect(valuesOf(form, signIn)).toEqual(wizardValues({
      authority: AUTHORITY, roleClaim: 'app_roles', extraScopes: ['offline_access', 'groups'], refuseGuests: false, clientSecretExpiresOn: '2027-06-30',
    }))
  })

  it('shows a deployment value locked and sends null for it', () => {
    const signIn = signInSettings({
      authority: { value: AUTHORITY, source: 'deployment' },
      refuseGuests: { value: false, source: 'deployment' },
      extraScopes: { value: ['email'], source: 'deployment' },
    })
    const form = newForm(signIn)
    expect(form.authority).toBe(AUTHORITY)
    expect(form.refuseGuests).toBe(false)
    expect(form.extraScopes).toBe('email')
    form.authority = 'https://other.example.org'
    const values = valuesOf(form, signIn)
    expect(values.authority).toBeNull()
    expect(values.refuseGuests).toBeNull()
    expect(values.extraScopes).toBeNull()
  })

  it('fills in the wizard\'s own values on a re-open, and leaves defaults empty', () => {
    const signIn = signInSettings({ authority: { value: AUTHORITY, source: 'wizard' }, roleClaim: { value: 'roles', source: 'default' } })
    const form = newForm(signIn)
    expect(form.authority).toBe(AUTHORITY)
    expect(form.roleClaim).toBe('')
  })

  it('has three secret states: null keeps the stored one, "" removes it, a value replaces it', () => {
    const signIn = signInSettings({ clientSecret: { set: true, source: 'wizard', setAt: null, expiresOn: null, unreadable: false } })
    const form = newForm(signIn)
    expect(form.clientSecret).toBe('')
    expect(secretOf(form, signIn)).toBeNull()
    form.clientSecret = 's3cret'
    expect(secretOf(form, signIn)).toBe('s3cret')
    form.removeSecret = true
    expect(secretOf(form, signIn)).toBe('')
    expect(valuesOf({ ...form, clientSecretExpiresOn: '2027-01-01' }, signIn).clientSecretExpiresOn).toBeNull()
    form.removeSecret = false
    form.clientSecret = '   '
    expect(secretOf(form, signIn)).toBeNull()
  })

  it('never sends a secret the deployment sets', () => {
    const signIn = signInSettings({ clientSecret: { set: true, source: 'deployment', setAt: null, expiresOn: null, unreadable: false } })
    const form = { ...newForm(signIn), clientSecret: 'typed', removeSecret: true, clientSecretExpiresOn: '2027-01-01' }
    expect(secretOf(form, signIn)).toBeNull()
    expect(valuesOf(form, signIn).clientSecretExpiresOn).toBeNull()
  })

  it('fills the form from the tested values on a reload, so the test still counts', () => {
    const signIn = signInSettings()
    const test = setupTest({ values: wizardValues({ authority: AUTHORITY, tenantId: 'tid', refuseGuests: false }), clientSecret: 'removed' })
    const form = newForm(signIn, test)
    expect(form).toMatchObject({ authority: AUTHORITY, tenantId: 'tid', refuseGuests: false, removeSecret: true, clientSecret: '' })
    expect(isStale(test, form, signIn, false)).toBe(false)
    expect(presetOf(form)).toBe('entra')
  })

  it('marks a finished test stale when a value or the secret changed after it', () => {
    const signIn = signInSettings()
    const test = setupTest()
    const form = newForm(signIn, test)
    expect(isStale(test, form, signIn, false)).toBe(false)
    expect(isStale(test, form, signIn, true)).toBe(true)
    form.clientId = 'another-client'
    expect(isStale(test, form, signIn, false)).toBe(true)
    form.clientId = '  '
    expect(isStale(test, form, signIn, false)).toBe(false)
    expect(isStale({ ...test, status: 'pending' }, { ...form, clientId: 'x' }, signIn, true)).toBe(false)
  })

  it('offers Finish only for a passed, current test by a platform admin', () => {
    const test = setupTest()
    expect(canFinish(test, false)).toBe(true)
    expect(canFinish(test, true)).toBe(false)
    expect(canFinish(null, false)).toBe(false)
    expect(canFinish({ ...test, status: 'failed', reason: 'token_refused' }, false)).toBe(false)
    expect(canFinish({ ...test, status: 'pending' }, false)).toBe(false)
    expect(canFinish({ ...test, result: { ...test.result!, isPlatformAdmin: false } }, false)).toBe(false)
    expect(canFinish({ ...test, values: null }, false)).toBe(false)
  })

  it('completes with the tested values and the test id, without a secret', () => {
    const test = setupTest({ values: wizardValues({ authority: AUTHORITY, extraScopes: ['groups'] }) })
    const body = completeRequestOf(test as Parameters<typeof completeRequestOf>[0])
    expect(body).toEqual({ ...wizardValues({ authority: AUTHORITY, extraScopes: ['groups'] }), testId: 't1' })
    expect(body).not.toHaveProperty('clientSecret')
  })

  it('applies a preset to the Advanced values that are not locked', () => {
    const signIn = signInSettings({ groupClaim: { value: 'member_of', source: 'deployment' } })
    const form = { ...newForm(signIn), roleClaim: 'x', groupClaim: 'member_of', tenantId: 'tid', extraScopes: 'a', refuseGuests: false }
    applyPreset(form, signIn, 'entra')
    expect(form).toMatchObject({ roleClaim: '', groupClaim: 'member_of', tenantId: 'tid', extraScopes: '', refuseGuests: true })
    applyPreset(form, signIn, 'keycloak')
    expect(form.tenantId).toBe('')
    expect(valuesOf(form, signIn)).toEqual(wizardValues())
  })
})

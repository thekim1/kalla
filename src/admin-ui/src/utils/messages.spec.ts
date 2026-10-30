import { describe, expect, it } from 'vitest'
import raw from '../../../../contracts/admin/v1/error-codes.json?raw'
import { ERROR_MESSAGES, formatMessage } from './messages'

// The exported list of the admin API's error codes (admin design §4.1); CI fails when the UI misses one.
const contract = JSON.parse(raw) as { codes: { code: string; message: string }[] }

describe('error messages', () => {
  it('has a Swedish text for every error code of the API, and no others', () => {
    expect(Object.keys(ERROR_MESSAGES).sort()).toEqual(contract.codes.map((c) => c.code).sort())
  })

  it('uses the same placeholders as the English message', () => {
    const names = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort()
    for (const { code, message } of contract.codes) expect(names(ERROR_MESSAGES[code]!), code).toEqual(names(message))
  })

  it('fills placeholders and drops missing ones', () => {
    expect(formatMessage('Regel ({constraint}).', { constraint: 'CK_X' })).toBe('Regel (CK_X).')
    expect(formatMessage('Regel ({constraint}).')).toBe('Regel ().')
  })
})

import { describe, expect, it } from 'vitest'
import { ApiError } from '@/api/client'
import { englishDetail, mapFieldErrors, problemMessage } from './problem'

describe('problemMessage', () => {
  it('shows the UI text of the code with its params, never the API detail', () => {
    const error = new ApiError(400, 'POST', { code: 'platform_rule', params: { constraint: 'CK_Clients_History' }, detail: 'The change breaks a platform rule (CK_Clients_History).' })
    expect(problemMessage(error)).toBe('The change breaks a platform rule (CK_Clients_History). Check the highlighted fields.')
    expect(englishDetail(error)).toBeNull()
  })

  it('falls back to a text for the status, offering the API detail separately', () => {
    const error = new ApiError(409, 'PUT', { code: 'something_new', detail: 'A new rule.' })
    expect(problemMessage(error)).toBe('The change conflicts with something that already exists.')
    expect(englishDetail(error)).toBe('A new rule.')
    expect(problemMessage(new ApiError(0, 'GET', null))).toBe('The server cannot be reached. Check your connection and try again.')
    expect(problemMessage(new ApiError(503, 'GET', null))).toBe('The server had a problem. Try again in a moment.')
    expect(problemMessage(new Error('boom'))).toBe('Something went wrong. Try again.')
  })
})

describe('mapFieldErrors', () => {
  it('maps nested item errors such as Targets[0].weight onto the targets field', () => {
    const error = new ApiError(400, 'POST', { errors: { 'Targets[0].weight': ['The value is invalid.'], condition: ['Unknown variable (character 3)'] } })
    expect(mapFieldErrors(error, ['name', 'targets', 'condition'])).toEqual({ targets: 'The value is invalid.', condition: 'Unknown variable (character 3)' })
  })

  it('still matches plain and $.-prefixed names, ignoring case', () => {
    const error = new ApiError(400, 'POST', { errors: { Name: ['x'], '$.scopeId': ['y'] } })
    expect(mapFieldErrors(error, ['name', 'scopeId'])).toEqual({ name: 'x', scopeId: 'y' })
  })

  it('prefers the UI text of a field error code', () => {
    const error = new ApiError(400, 'POST', { errors: { kind: ['Kind must be one of …'] }, errorCodes: { kind: [{ code: 'invalid_value' }] } })
    expect(mapFieldErrors(error, ['kind'])).toEqual({ kind: 'The value is not valid. Check the field.' })
  })
})

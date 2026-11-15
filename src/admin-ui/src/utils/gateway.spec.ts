import { describe, expect, it } from 'vitest'
import { gatewayHref } from './gateway'

describe('gatewayHref', () => {
  it('appends the path to an http(s) address without doubling the slash', () => {
    expect(gatewayHref('https://llm.example.se/', '/keys')).toBe('https://llm.example.se/keys')
    expect(gatewayHref('HTTP://llm.example.se//', '/usage')).toBe('HTTP://llm.example.se/usage')
    expect(gatewayHref('https://llm.example.se/admin', '/organisation')).toBe('https://llm.example.se/admin/organisation')
  })

  it('gives no link for a missing address or one that is not http(s)', () => {
    expect(gatewayHref(null, '/keys')).toBeNull()
    expect(gatewayHref(undefined, '/keys')).toBeNull()
    expect(gatewayHref('', '/keys')).toBeNull()
    expect(gatewayHref('javascript:alert(1)//', '/keys')).toBeNull()
    expect(gatewayHref('JavaScript:alert(1)', '/keys')).toBeNull()
    expect(gatewayHref(' https://llm.example.se', '/keys')).toBeNull()
    expect(gatewayHref('data:text/html,<script>alert(1)</script>', '/keys')).toBeNull()
    expect(gatewayHref('//llm.example.se', '/keys')).toBeNull()
  })
})

import { describe, expect, it } from 'vitest'
import type { AttentionItem } from '@/api/types'
import { ATTENTION } from '@/utils/labels'
import { attentionLink } from './attention'

describe('the downvoted-document attention item (admin design §5.5.1, §8.8, B-66)', () => {
  const item: AttentionItem = {
    kind: 'document_downvoted', objectType: 'collection', objectId: 'c1', objectName: 'Bygglov', departmentId: 'd1', since: null,
    details: { documentId: 'doc1', documentTitle: 'Altan och uteplats', downvotes: '3', days: '7' },
  }

  it('names the document, its knowledge base and the downvotes, and opens the knowledge base\'s Overview', () => {
    expect(ATTENTION.kinds.document_downvoted!(item.details, item.objectName)).toBe('Cited in 3 downvoted answers in the last 7 days: Altan och uteplats in Bygglov')
    expect(attentionLink(item)).toEqual({ name: 'collection', params: { id: 'c1' }, query: { tab: 'overview' } })
  })
})

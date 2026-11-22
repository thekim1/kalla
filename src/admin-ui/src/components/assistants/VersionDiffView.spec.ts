import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import type { VersionDiff } from '@/api/types'
import { VERSION_DIFF } from '@/utils/labels'
import VersionDiffView from './VersionDiffView.vue'

const noText = { changed: false, tooLong: false, lines: [] }
const base: VersionDiff = {
  from: 1, to: 2, toDraft: false, changed: false, settings: [], collectionsAdded: [], collectionsRemoved: [], topicsChanged: [],
  instructions: noText, notFoundSv: noText, notFoundEn: noText,
}

describe('The version diff (admin design §4.5, §5.5.4; B-56)', () => {
  it('names settings in plain words, lists knowledge bases, and marks lines as <ins>/<del> with a sign, not by colour alone', () => {
    const wrapper = mount(VersionDiffView, { props: { diff: {
      ...base, changed: true,
      settings: [{ name: 'model', from: 'fors/chat-eu', to: null }, { name: 'querySteps.retrieval.topK', from: 8, to: 10 }],
      collectionsAdded: [{ collectionId: 'c1', name: 'Avfall', tags: [] }],
      collectionsRemoved: [{ collectionId: 'c2', name: null, tags: [] }],
      topicsChanged: [{ collectionId: 'c3', name: 'Bygglov', from: [], to: ['altan'] }],
      instructions: { changed: true, tooLong: false, lines: [
        { op: 'equal', text: 'Du svarar om bygglov.' }, { op: 'delete', text: 'Svara kort.' }, { op: 'insert', text: 'Svara kort och vänligt.' },
      ] },
      notFoundSv: { changed: true, tooLong: true, lines: [] },
    } } })

    const rows = wrapper.findAll('[data-test=diff-settings] tbody tr').map((r) => r.findAll('th, td').map((c) => c.text()))
    expect(rows).toEqual([['Model', 'fors/chat-eu', VERSION_DIFF.none], ['querySteps.retrieval.topK', '8', '10']])
    expect(wrapper.get('[data-test=diff-knowledge]').text()).toContain(VERSION_DIFF.added('Avfall'))
    expect(wrapper.get('[data-test=diff-knowledge]').text()).toContain(VERSION_DIFF.removed(VERSION_DIFF.unknownCollection))
    expect(wrapper.get('[data-test=diff-knowledge]').text()).toContain(VERSION_DIFF.topics('Bygglov', VERSION_DIFF.allTopics, 'altan'))
    expect(wrapper.get('del').text()).toBe(`− ${VERSION_DIFF.deleted}: Svara kort.`)
    expect(wrapper.get('ins').text()).toBe(`+ ${VERSION_DIFF.inserted}: Svara kort och vänligt.`)
    expect(wrapper.text()).toContain(VERSION_DIFF.tooLong)
    expect(wrapper.find('[data-test=diff-nothing]').exists()).toBe(false)
  })

  it('says when nothing differs', () => {
    expect(mount(VersionDiffView, { props: { diff: base } }).get('[data-test=diff-nothing]').text()).toBe(VERSION_DIFF.nothing)
  })
})

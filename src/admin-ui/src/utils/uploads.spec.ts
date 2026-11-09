import { describe, expect, it } from 'vitest'
import { filesFromDrop, isNoise, skipReason } from './uploads'

const file = (name: string, bytes = 10) => new File(['x'.repeat(bytes)], name)

// A folder tree as the File and Directory Entries API hands it out; readEntries returns one page, then none.
const fileEntry = (f: File) => ({ isFile: true, isDirectory: false, name: f.name, file: (ok: (x: File) => void) => ok(f) })
const dirEntry = (name: string, children: unknown[]) => {
  let read = false
  return {
    isFile: false, isDirectory: true, name,
    createReader: () => ({ readEntries: (ok: (e: unknown[]) => void) => { ok(read ? [] : children); read = true } }),
  }
}
const transfer = (entries: unknown[]) => ({
  items: entries.map((e) => ({ kind: 'file', webkitGetAsEntry: () => e })),
  files: [],
}) as unknown as DataTransfer

describe('upload file choice (connectors §9.2)', () => {
  it('skips types the worker refuses, deferred formats, empty and too large files', () => {
    expect(skipReason(file('a.PDF'), 100)).toBeNull()
    expect(skipReason(file('a.zip'), 100)).toBe('type')
    expect(skipReason(file('README'), 100)).toBe('type')
    expect(skipReason(file('old.doc'), 100)).toBeNull()
    expect(skipReason(file('brev.RTF'), 100)).toBeNull()
    expect(skipReason(file('mejl.msg'), 100)).toBe('deferred')
    expect(skipReason(file('a.txt', 0), 100)).toBe('empty')
    expect(skipReason(file('a.txt', 101), 100)).toBe('too-large')
  })

  it('leaves out system and lock files', () => {
    expect(['.DS_Store', 'Thumbs.db', 'desktop.ini', '~$rapport.docx'].every(isNoise)).toBe(true)
    expect(isNoise('rapport.docx')).toBe(false)
  })

  it('walks dropped folders recursively', async () => {
    const tree = dirEntry('Blanketter', [
      fileEntry(file('a.pdf')),
      fileEntry(file('.DS_Store')),
      dirEntry('2026', [fileEntry(file('b.docx'))]),
      dirEntry('.git', [fileEntry(file('config'))]),
    ])
    const files = await filesFromDrop(transfer([tree, fileEntry(file('c.txt'))]))
    expect(files.map((f) => f.name)).toEqual(['a.pdf', 'b.docx', 'c.txt'])
  })
})

import { describe, expect, it } from 'vitest'
import { encodeFileName, filesFromDrop, filesFromInput, isNoise, skipReason } from './uploads'

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

  it('skips a path deeper or longer than the API takes (admin design §4.5)', () => {
    expect(skipReason(file('a.pdf'), 100, 'HR/Policys/a.pdf')).toBeNull()
    expect(skipReason(file('a.pdf'), 100, `${Array.from({ length: 10 }, (_, i) => `d${i}`).join('/')}/a.pdf`)).toBe('path')
    expect(skipReason(file('a.pdf'), 100, `${'x'.repeat(296)}/a.pdf`)).toBe('path')
    expect(skipReason(file('a.pdf'), 100, `${'x'.repeat(256)}/a.pdf`)).toBe('path')
  })

  it('percent-encodes each segment of the path and keeps the slashes', () => {
    expect(encodeFileName('HR/Policys 2026/semester å.pdf')).toBe('HR/Policys%202026/semester%20%C3%A5.pdf')
    expect(encodeFileName('a#b.pdf')).toBe('a%23b.pdf')
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
    expect(files.map((f) => f.file.name)).toEqual(['a.pdf', 'b.docx', 'c.txt'])
    // Each file's path from the dropped folder, that folder's name included; a loose file has just its name (§5.5.7).
    expect(files.map((f) => f.path)).toEqual(['Blanketter/a.pdf', 'Blanketter/2026/b.docx', 'c.txt'])
  })

  it('gives a dropped file just its name without the entries API', async () => {
    const plain = { items: [{ kind: 'file' }], files: [file('a.pdf'), file('Thumbs.db')] } as unknown as DataTransfer
    expect(await filesFromDrop(plain)).toEqual([{ file: expect.any(File), path: 'a.pdf' }])
  })

  it('takes the folder picker\'s relative paths and leaves out noise in any folder of them', () => {
    const picked = (name: string, relative: string) => Object.defineProperty(file(name), 'webkitRelativePath', { value: relative })
    const input = { files: [picked('a.pdf', 'HR/Policys/a.pdf'), picked('config', 'HR/.git/config'), picked('b.pdf', ''), picked('.DS_Store', 'HR/.DS_Store')] }
    expect(filesFromInput(input as unknown as HTMLInputElement).map((p) => p.path)).toEqual(['HR/Policys/a.pdf', 'b.pdf'])
  })
})

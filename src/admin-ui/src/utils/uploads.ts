// Choosing files for an upload source (admin design §4.5, §5.5.7, connectors §9.1–§9.2): files dropped or picked one by
// one, whole folders (drag and drop or the folder picker) with each file's relative path, the pre-check of type, size and
// path before anything is sent, and the split into batches of the API's limits. The admin API checks them again; this
// only saves round trips.

/** Extensions the worker accepts (mirror of `UploadEndpoints.Allowed`). */
export const UPLOAD_EXTENSIONS: ReadonlySet<string> = new Set([
  '.pdf', '.docx', '.pptx', '.xlsx', '.odt', '.ods', '.odp', '.jpg', '.jpeg', '.png', '.tif', '.tiff', '.txt', '.md', '.csv', '.html', '.htm',
  '.doc', '.xls', '.ppt', '.rtf',
])

/** Extensions refused as not supported yet (mirror of `UploadEndpoints.Deferred`). */
export const DEFERRED_EXTENSIONS: ReadonlySet<string> = new Set(['.eml', '.msg'])

/** The `accept` list of the file pickers. */
export const UPLOAD_ACCEPT = [...UPLOAD_EXTENSIONS, ...DEFERRED_EXTENSIONS].join(',')

export type SkipReason = 'type' | 'deferred' | 'too-large' | 'empty' | 'path'

/** The API's limits on a relative path in `X-File-Name` (admin design §4.5). */
export const MAX_PATH_LENGTH = 300
export const MAX_PATH_SEGMENTS = 10
export const MAX_SEGMENT_LENGTH = 255

/** A file to upload with its relative path (`HR/Policys/semester.pdf`); a file picked or dropped on its own has just its name. */
export interface PickedFile {
  file: File
  path: string
}

export const extensionOf = (name: string): string => {
  const dot = name.lastIndexOf('.')
  return dot > 0 ? name.slice(dot).toLowerCase() : ''
}

/** System and lock files that a folder carries but nobody means to upload: left out without a word. */
export function isNoise(name: string): boolean {
  const lower = name.toLowerCase()
  return lower.startsWith('.') || lower.startsWith('~$') || lower === 'thumbs.db' || lower === 'desktop.ini'
}

/** Why a file is not sent, or null when it may be; `path` is its relative path (its name when it has no folder). */
export function skipReason(file: File, maxFileBytes: number, path = file.name): SkipReason | null {
  const extension = extensionOf(file.name)
  if (DEFERRED_EXTENSIONS.has(extension) && !UPLOAD_EXTENSIONS.has(extension)) return 'deferred'
  if (!UPLOAD_EXTENSIONS.has(extension)) return 'type'
  if (file.size === 0) return 'empty'
  if (file.size > maxFileBytes) return 'too-large'
  const segments = path.split('/')
  if (path.length > MAX_PATH_LENGTH || segments.length > MAX_PATH_SEGMENTS || segments.some((s) => s.length > MAX_SEGMENT_LENGTH)) return 'path'
  return null
}

/** The `X-File-Name` header: the relative path, each segment percent-encoded as UTF-8, `/` between them. */
export const encodeFileName = (path: string): string => path.split('/').map(encodeURIComponent).join('/')

// The File and Directory Entries API as browsers ship it (`webkitGetAsEntry`); typed here since lib.dom's names vary.
interface Entry { isFile: boolean; isDirectory: boolean; name: string }
interface FileEntry extends Entry { file(ok: (f: File) => void, fail: (e: unknown) => void): void }
interface DirectoryEntry extends Entry { createReader(): { readEntries(ok: (entries: Entry[]) => void, fail: (e: unknown) => void): void } }

/** Walks an entry; `folder` is the path of the folder it is in ('' at the top of the drop). */
async function walk(entry: Entry, folder: string, out: PickedFile[]): Promise<void> {
  if (isNoise(entry.name)) return
  const path = folder ? `${folder}/${entry.name}` : entry.name
  if (entry.isFile) {
    out.push({ file: await new Promise<File>((ok, fail) => (entry as FileEntry).file(ok, fail)), path })
    return
  }
  if (!entry.isDirectory) return
  const reader = (entry as DirectoryEntry).createReader()
  // readEntries returns at most 100 entries per call (Chromium): read until it returns none.
  for (;;) {
    const entries = await new Promise<Entry[]>((ok, fail) => reader.readEntries(ok, fail))
    if (entries.length === 0) return
    for (const child of entries) await walk(child, path, out)
  }
}

/** Every file in a drop with its path from the dropped folder (that folder's name included), noise left out. */
export async function filesFromDrop(data: DataTransfer): Promise<PickedFile[]> {
  const items = [...(data.items ?? [])].filter((i) => i.kind === 'file')
  const entries = items.map((i) => (i as DataTransferItem & { webkitGetAsEntry?: () => Entry | null }).webkitGetAsEntry?.() ?? null)
  if (entries.length === 0 || entries.some((e) => e === null)) {
    // No entries API (or not for every item): the plain file list, which holds no folders' contents.
    return [...(data.files ?? [])].filter((f) => !isNoise(f.name)).map((file) => ({ file, path: file.name }))
  }
  const out: PickedFile[] = []
  for (const entry of entries) await walk(entry!, '', out)
  return out
}

/**
 * Files from a picker with their paths: the folder picker (`webkitdirectory`) gives every file of the tree with its
 * `webkitRelativePath` (the picked folder's name first); the file picker gives just names. Noise, also in a folder of
 * the path (`.git/config`), is left out.
 */
export const filesFromInput = (input: HTMLInputElement): PickedFile[] =>
  [...(input.files ?? [])]
    .map((file) => ({ file, path: file.webkitRelativePath || file.name }))
    .filter((p) => !p.path.split('/').some(isNoise))

// Choosing files for an upload source (admin design §4.5, connectors §9.1–§9.2): files dropped or picked one by one,
// whole folders (drag and drop or the folder picker), the pre-check of type and size before anything is sent, and the
// split into batches of the API's limits. The admin API checks type and size again; this only saves round trips.

/** Extensions the worker accepts (mirror of `UploadEndpoints.Allowed`). */
export const UPLOAD_EXTENSIONS: ReadonlySet<string> = new Set([
  '.pdf', '.docx', '.pptx', '.xlsx', '.odt', '.ods', '.odp', '.jpg', '.jpeg', '.png', '.tif', '.tiff', '.txt', '.md', '.csv', '.html', '.htm',
  '.doc', '.xls', '.ppt', '.rtf',
])

/** Extensions refused as not supported yet (mirror of `UploadEndpoints.Deferred`). */
export const DEFERRED_EXTENSIONS: ReadonlySet<string> = new Set(['.eml', '.msg'])

/** The `accept` list of the file pickers. */
export const UPLOAD_ACCEPT = [...UPLOAD_EXTENSIONS, ...DEFERRED_EXTENSIONS].join(',')

export type SkipReason = 'type' | 'deferred' | 'too-large' | 'empty'

export const extensionOf = (name: string): string => {
  const dot = name.lastIndexOf('.')
  return dot > 0 ? name.slice(dot).toLowerCase() : ''
}

/** System and lock files that a folder carries but nobody means to upload: left out without a word. */
export function isNoise(name: string): boolean {
  const lower = name.toLowerCase()
  return lower.startsWith('.') || lower.startsWith('~$') || lower === 'thumbs.db' || lower === 'desktop.ini'
}

/** Why a file is not sent, or null when it may be. */
export function skipReason(file: File, maxFileBytes: number): SkipReason | null {
  const extension = extensionOf(file.name)
  if (DEFERRED_EXTENSIONS.has(extension) && !UPLOAD_EXTENSIONS.has(extension)) return 'deferred'
  if (!UPLOAD_EXTENSIONS.has(extension)) return 'type'
  if (file.size === 0) return 'empty'
  if (file.size > maxFileBytes) return 'too-large'
  return null
}

// The File and Directory Entries API as browsers ship it (`webkitGetAsEntry`); typed here since lib.dom's names vary.
interface Entry { isFile: boolean; isDirectory: boolean; name: string }
interface FileEntry extends Entry { file(ok: (f: File) => void, fail: (e: unknown) => void): void }
interface DirectoryEntry extends Entry { createReader(): { readEntries(ok: (entries: Entry[]) => void, fail: (e: unknown) => void): void } }

async function walk(entry: Entry, out: File[]): Promise<void> {
  if (isNoise(entry.name)) return
  if (entry.isFile) {
    out.push(await new Promise<File>((ok, fail) => (entry as FileEntry).file(ok, fail)))
    return
  }
  if (!entry.isDirectory) return
  const reader = (entry as DirectoryEntry).createReader()
  // readEntries returns at most 100 entries per call (Chromium): read until it returns none.
  for (;;) {
    const entries = await new Promise<Entry[]>((ok, fail) => reader.readEntries(ok, fail))
    if (entries.length === 0) return
    for (const child of entries) await walk(child, out)
  }
}

/** Every file in a drop, folders walked recursively, noise left out. */
export async function filesFromDrop(data: DataTransfer): Promise<File[]> {
  const items = [...(data.items ?? [])].filter((i) => i.kind === 'file')
  const entries = items.map((i) => (i as DataTransferItem & { webkitGetAsEntry?: () => Entry | null }).webkitGetAsEntry?.() ?? null)
  if (entries.length === 0 || entries.some((e) => e === null)) {
    // No entries API (or not for every item): the plain file list, which holds no folders' contents.
    return [...(data.files ?? [])].filter((f) => !isNoise(f.name))
  }
  const out: File[] = []
  for (const entry of entries) await walk(entry!, out)
  return out
}

/** Files from a picker (`webkitdirectory` gives every file of the folder tree), noise left out. */
export const filesFromInput = (input: HTMLInputElement): File[] => [...(input.files ?? [])].filter((f) => !isNoise(f.name))

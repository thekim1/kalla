import { api } from '@/api'
import type { Collection, Source, UploadFile } from '@/api/types'
import type { Tone } from '@/utils/labels'
import { FILES } from '@/utils/labels'
import { formatDecimal } from '@/utils/format'

// Helpers of the Files experience (admin design §5.5.7): sizes, state tones, folders of a relative path, the live files an
// upload would replace, and the upload source a knowledge base of files gets.

/** How often the summary and the visible rows refresh while files are processing (admin design §5.5.7). */
export const POLL_MS = 3000

/** The grace of a removed upload (connectors §9.3, §13.4); the API checks it again. */
const RESTORE_GRACE_MS = 14 * 24 * 60 * 60 * 1000

export const restorable = (f: UploadFile): boolean => f.deletedAt !== null && Date.now() - Date.parse(f.deletedAt) < RESTORE_GRACE_MS

export const sizeText = (bytes: number): string =>
  bytes === 0 ? '0 kB' : bytes < 1024 * 1024 ? `${Math.max(1, Math.round(bytes / 1024))} kB` : `${formatDecimal(bytes / 1024 / 1024, 1)} MB`

/** The file's bytes were not checked by a malware scan: `unscanned`, or `indexed` without `scannedAt` (admin design §5.5.7). */
export const notScanned = (f: UploadFile): boolean => f.state === 'unscanned' || (f.state === 'indexed' && !f.scannedAt)

export const stateTone = (state: string): Tone => (state === 'indexed' ? 'ok' : state === 'malware' || state === 'refused' ? 'danger' : 'neutral')

/** The folder of a relative path (`HR/Policys` of `HR/Policys/semester.pdf`), '' for a file without one. */
export const folderOf = (path: string): string => {
  const slash = path.lastIndexOf('/')
  return slash < 0 ? '' : path.slice(0, slash)
}

/** The file's own name, without its folder. */
export const baseName = (path: string): string => path.slice(path.lastIndexOf('/') + 1)

/** A relative path as the admin API stores it (`UploadEndpoints.FileName`): `\` read as `/`, no leading `/`, each segment trimmed. */
export const storedPath = (path: string): string =>
  path.trim().replace(/\\/g, '/').replace(/^\/+/, '').split('/').map((s) => s.trim()).join('/')

/** What two paths of the same file share: the stored path, case ignored (as Windows and macOS folders ignore it). */
export const pathKey = (path: string): string => storedPath(path).toLowerCase()

/** The longest start every path shares ('' when they share none). */
export function commonStart(paths: string[]): string {
  let start = paths[0] ?? ''
  for (const path of paths) {
    let n = 0
    while (n < start.length && n < path.length && start[n] === path[n]) n++
    start = start.slice(0, n)
  }
  return start
}

/** What an upload does with added files that match live ones (admin design §5.5.7 point 6): replace them, or keep both. */
export type KnownChoice = 'replace' | 'keep'

/** Rows per request when the source's files are looked up by path: the admin API's largest page. */
const LOOKUP_PAGE = 200

/**
 * The source's live files whose path is one of `paths` (admin design §5.5.7 point 6), keyed by {@link pathKey}; the newest
 * when two share a path. The Files card has loaded only its first rows, so this reads the list again, filtered on the
 * start the paths share (one file's whole path; a dropped folder's name), page by page.
 */
export async function liveFilesByPath(sourceId: string, paths: string[]): Promise<Map<string, UploadFile>> {
  const wanted = new Set(paths.map(pathKey))
  const found = new Map<string, UploadFile>()
  const q = commonStart(paths.map(storedPath)).trim() || undefined
  let after: string | null = null
  do {
    const page = await api.sources.files(sourceId, { q, sort: 'name', after: after ?? undefined, limit: LOOKUP_PAGE })
    for (const file of page.items) {
      const key = pathKey(file.fileName)
      const known = found.get(key)
      if (wanted.has(key) && (!known || Date.parse(file.uploadedAt) > Date.parse(known.uploadedAt))) found.set(key, file)
    }
    after = page.items.length ? page.next : null
  } while (after)
  return found
}

export interface FolderGroup {
  folder: string
  files: UploadFile[]
}

/** Rows grouped by folder: files without a folder first, then each folder in the order its first file came. */
export function groupByFolder(files: UploadFile[]): FolderGroup[] {
  const groups = new Map<string, UploadFile[]>([['', []]])
  for (const file of files) {
    const folder = folderOf(file.fileName)
    const group = groups.get(folder)
    if (group) group.push(file)
    else groups.set(folder, [file])
  }
  return [...groups].map(([folder, list]) => ({ folder, files: list })).filter((g) => g.files.length > 0)
}

/**
 * The upload source of a knowledge base of files: named Files, the collection's level, the default template; a public
 * collection's personal-data mode is the source drawer's default (hold).
 */
export const createUploadSource = (collection: Pick<Collection, 'id' | 'classification'>): Promise<Source> =>
  api.collections.addSource(collection.id, {
    type: 'upload', name: FILES.sourceName, classification: collection.classification,
    settings: collection.classification === 'public' ? { piiMode: 'hold' } : {},
  })

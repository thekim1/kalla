import { api } from '@/api'
import type { Collection, Source, UploadFile } from '@/api/types'
import type { Tone } from '@/utils/labels'
import { FILES } from '@/utils/labels'
import { formatDecimal } from '@/utils/format'

// Helpers of the Files experience (admin design §5.5.7): sizes, state tones, folders of a relative path, and the upload
// source a knowledge base of files gets.

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

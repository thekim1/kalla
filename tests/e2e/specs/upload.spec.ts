import { mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect, test } from '@playwright/test'
import { accepted, eventually, getJson, login } from '../helpers'

// Uploads into a knowledge base of files (admin design §5.5.7): a file whose name and folder match a live file of the source
// replaces it by default, so the document keeps its id (point 6; support case SA-11, B-83); Keep both adds a new file; the
// same name in a folder is another file. Runs on the FakeGateway stack with the real worker.

interface UploadFile { id: string; fileName: string; state: string; documentId: string | null; replacedAt: string | null }

const markdown = (name: string, version: number) => ({ name, mimeType: 'text/markdown', buffer: Buffer.from(`# Lösenordsrutin\n\nVersion ${version} av rutinen.\n`) })

test.afterEach(async ({ page }) => { await page.close() })

test('a known file name replaces the file and keeps its document; Keep both adds one; a folder is part of the name', async ({ page }) => {
  const collection = `Uppladdning ${crypto.randomUUID().slice(0, 8)}`
  await login(page, 'rag-department-admin')

  // A knowledge base of files: the create drawer makes the upload source and opens the file list.
  await page.getByRole('navigation', { name: 'Main menu' }).getByRole('link', { name: 'Knowledge bases' }).click()
  await page.getByRole('main').getByRole('button', { name: 'Create knowledge base' }).click()
  const create = page.getByRole('dialog', { name: 'Create knowledge base' })
  await create.getByRole('textbox', { name: 'Name', exact: true }).fill(collection)
  await create.getByRole('combobox', { name: 'Department' }).selectOption({ label: 'Kommunledningskontoret' })
  await create.getByRole('radio', { name: /^Internal/ }).check()
  await create.getByRole('radio', { name: /^Files I upload/ }).check()
  await create.getByRole('button', { name: 'Create knowledge base' }).click()
  await expect(create).toBeHidden()
  await expect(page.locator('#page-title')).toHaveText(collection)
  const files = page.getByTestId('files')
  await expect(files.getByTestId('drop-zone')).toBeVisible()

  // The first version: a new file; the batch's URL names the source.
  const batch = page.waitForRequest(r => r.method() === 'POST' && /\/api\/sources\/[^/]+\/upload-batches$/.test(new URL(r.url()).pathname))
  await files.getByTestId('file-input').setInputFiles(markdown('rutin.md', 1))
  await accepted(page, /^\/api\/upload-batches\/[^/]+\/commit$/, () => files.getByTestId('upload').click())
  const sourceId = new URL((await batch).url()).pathname.split('/')[3]!
  const live = async (): Promise<UploadFile[]> => (await getJson(page, `/api/sources/${sourceId}/files?limit=200`)).items
  await eventually(async () => (await live()).some(f => f.fileName === 'rutin.md' && f.state === 'indexed'), 'rutin.md is indexed')
  const [first] = await live()

  // The second version with the same name: one dialog lists it, Replace is the default, and the slot and document stay.
  await files.getByTestId('file-input').setInputFiles(markdown('rutin.md', 2))
  await files.getByTestId('upload').click()
  const known = page.getByRole('dialog', { name: 'This file is already uploaded' })
  await expect(known.getByRole('list', { name: 'Files already uploaded' })).toHaveText('rutin.md')
  const replace = known.getByRole('button', { name: 'Replace the existing file' })
  await expect(replace).toBeFocused()
  await accepted(page, /^\/api\/upload-batches\/[^/]+\/commit$/, () => replace.click())
  await expect(known).toBeHidden()
  await eventually(async () => {
    const now = await live()
    return now.length === 1 && now[0]!.id === first!.id && now[0]!.replacedAt !== null && now[0]!.state === 'indexed'
  }, 'rutin.md is replaced in its slot')
  expect((await live())[0]!.documentId).toBe(first!.documentId)

  // The same name in a folder is another file: no dialog.
  const folder = join(test.info().outputPath('upload'), 'Rutiner')
  mkdirSync(folder, { recursive: true })
  const inFolder = markdown('rutin.md', 3)
  writeFileSync(join(folder, inFolder.name), inFolder.buffer)
  await files.getByTestId('folder-input').setInputFiles(folder)
  await expect(files.getByTestId('pending')).toContainText('Rutiner/rutin.md')
  await accepted(page, /^\/api\/upload-batches\/[^/]+\/commit$/, () => files.getByTestId('upload').click())
  await expect(known).toBeHidden()

  // Keep both: a new file with the known name.
  await files.getByTestId('file-input').setInputFiles(markdown('rutin.md', 4))
  await files.getByTestId('upload').click()
  await accepted(page, /^\/api\/upload-batches\/[^/]+\/commit$/, () => known.getByRole('button', { name: 'Keep both' }).click())
  await eventually(async () => {
    const names = (await live()).map(f => f.fileName).sort()
    return JSON.stringify(names) === JSON.stringify(['rutin.md', 'rutin.md', 'Rutiner/rutin.md'].sort())
  }, 'Keep both and the folder add two files')
  expect((await live()).filter(f => f.id === first!.id)).toHaveLength(1)
})

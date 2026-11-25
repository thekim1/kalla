import { mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect, test, type Page } from '@playwright/test'
import { accepted, eventually, getJson, login } from '../helpers'

// Topics picked from the sections found (B-64) and the topic form (B-65; admin design §5.5.2, Decisions "Topics
// editor"): a knowledge base of files with two folders of five files suggests both folders; ticking them and Add 2
// topics plans the processing once and confirms it in one dialog with the plan's count; the topics read pending, then
// are counted after the re-chunk. A file-type topic is added through the form with its match count, Edit opens a
// stored rule in words, and Remove takes a topic away. Runs on the FakeGateway stack with the real worker, on a
// knowledge base it creates itself.

interface UploadFile { fileName: string; state: string }
interface Topic { tag: string; documents: number | null; pending: boolean }

test.afterEach(async ({ page }) => { await page.close() })

/** A full-page screenshot kept in the test's output folder and attached to the report. */
async function shot(page: Page, name: string): Promise<void> {
  const path = test.info().outputPath(`${name}.png`)
  await page.screenshot({ path, fullPage: true })
  await test.info().attach(name, { path, contentType: 'image/png' })
}

test('two folders picked as topics are saved through one plan, pending, then counted after the re-chunk', async ({ page }) => {
  test.setTimeout(8 * 60_000)
  // Not first by name: the accessibility spec scans the first knowledge base of the list (#1064).
  const collection = `Topics ${crypto.randomUUID().slice(0, 8)}`
  await login(page, 'rag-department-admin')

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

  // Two folders of five files each: Bygglov/ and Skola/.
  let sourceId = ''
  for (const [folderName, subject] of [['Bygglov', 'bygglov'], ['Skola', 'skolval']] as const) {
    const folder = join(test.info().outputPath('topics'), folderName)
    mkdirSync(folder, { recursive: true })
    for (let i = 1; i <= 5; i++) writeFileSync(join(folder, `${subject}-${i}.md`), `# ${folderName} ${i}\n\nOm ${subject}, del ${i}.\n`)
    const batch = page.waitForRequest(r => r.method() === 'POST' && /\/api\/sources\/[^/]+\/upload-batches$/.test(new URL(r.url()).pathname))
    await files.getByTestId('folder-input').setInputFiles(folder)
    await expect(files.getByTestId('pending')).toContainText(`${folderName}/${subject}-1.md`)
    await accepted(page, /^\/api\/upload-batches\/[^/]+\/commit$/, () => files.getByTestId('upload').click())
    sourceId = new URL((await batch).url()).pathname.split('/')[3]!
  }
  await eventually(async () => {
    const items: UploadFile[] = (await getJson(page, `/api/sources/${sourceId}/files?limit=200`)).items
    return items.length === 10 && items.every(f => f.state === 'indexed')
  }, 'the ten files are indexed')

  // The Topics tab suggests both folders, each with its proposed topic.
  const collectionId = new URL(page.url()).pathname.split('/')[2]!
  await page.goto(`/collections/${collectionId}?tab=topics`)
  const suggested = page.getByTestId('topic-suggestions')
  await expect(suggested.getByTestId('topic-section')).toHaveCount(2)
  await suggested.getByRole('checkbox', { name: 'Add Bygglov/ as a topic' }).check()
  await suggested.getByRole('checkbox', { name: 'Add Skola/ as a topic' }).check()
  await expect(suggested.getByRole('textbox', { name: 'Topic for Bygglov/' })).toHaveValue('bygglov')
  await suggested.getByRole('textbox', { name: 'Topic for Skola/' }).fill('skola')
  await page.getByTestId('add-topics').click()

  // One plan for the processing (copied for the department on first publication), its own count, one confirm.
  const dialog = page.getByRole('dialog', { name: 'Add 2 topics?' })
  await expect(dialog.getByTestId('topics-plan-item')).toHaveCount(1, { timeout: 60_000 })
  await expect(dialog.getByTestId('topics-plan-item')).toContainText('saving processes 10 documents again')
  await shot(page, '1-confirm')
  await dialog.getByRole('button', { name: 'Confirm' }).click()
  await expect(dialog).toBeHidden()

  // Pending until the re-chunk is done, then counted.
  const topics = async (): Promise<Topic[]> => (await getJson(page, `/api/collections/${collectionId}/topics`)).topics
  const first = await topics()
  if (first.some(t => t.pending)) {
    await page.reload()
    await expect(page.getByTestId('topic-pending').first()).toBeVisible()
    await shot(page, '2-pending')
  }
  expect(first.filter(t => t.pending || (t.documents ?? 0) > 0).map(t => t.tag).sort()).toEqual(['bygglov', 'skola'])
  await eventually(async () => {
    const now = await topics()
    return now.find(t => t.tag === 'bygglov')?.documents === 5 && now.find(t => t.tag === 'skola')?.documents === 5 && !now.some(t => t.pending)
  }, 'both topics are counted after the re-chunk')
  await page.reload()
  const list = page.getByTestId('topics')
  await expect(list.getByRole('row').filter({ hasText: 'bygglov' })).toContainText('under Bygglov/')
  await expect(list.getByRole('row').filter({ hasText: 'skola' })).toContainText('5')
  // The picked folders are now covered: ticked, with their topic.
  await expect(page.getByTestId('topic-covered')).toHaveCount(2)
  await shot(page, '3-counted')

  // B-65: a topic through the form, with what it matches before it is saved.
  await page.getByTestId('add-topic').click()
  const form = page.getByRole('dialog', { name: 'Add topic' })
  await form.getByRole('textbox', { name: 'Topic' }).fill('rutiner')
  await form.getByRole('radio', { name: 'of the file type' }).check()
  await form.getByRole('combobox', { name: 'File type' }).selectOption('text/markdown')
  await expect(form.getByTestId('topic-preview-count')).toHaveText(/^Matches 10 of 10 documents$/)
  await shot(page, '4-form')
  await form.getByTestId('topic-form-save').click()
  const added = page.getByRole('dialog', { name: 'Add 1 topic?' })
  await expect(added.getByTestId('topics-plan-item')).toContainText('saving processes 10 documents again', { timeout: 60_000 })
  await added.getByRole('button', { name: 'Confirm' }).click()
  await expect(added).toBeHidden()
  await eventually(async () => (await topics()).find(t => t.tag === 'rutiner')?.documents === 10, 'the file type topic is counted')

  // Edit opens the stored rule in words.
  await page.reload()
  await page.getByRole('button', { name: 'Edit bygglov: under Bygglov/' }).click()
  const edit = page.getByRole('dialog', { name: 'Edit topic' })
  await expect(edit.getByRole('textbox', { name: 'Address or folder' })).toHaveValue('Bygglov/')
  await expect(edit.getByTestId('topic-preview-count')).toHaveText(/^Matches 5 of 10 documents$/)
  await edit.getByRole('button', { name: 'Cancel' }).click()

  // Remove: the topic goes once the documents are processed again, and its folder can be picked again.
  await page.getByRole('button', { name: 'Actions for skola' }).click()
  await page.getByRole('menuitem', { name: 'Remove topic' }).click()
  const removal = page.getByRole('dialog', { name: 'Remove the topic skola?' })
  await expect(removal.getByTestId('topics-plan-item')).toHaveCount(1, { timeout: 60_000 })
  await removal.getByRole('button', { name: 'Confirm' }).click()
  await expect(removal).toBeHidden()
  await eventually(async () => !(await topics()).some(t => t.tag === 'skola'), 'skola is gone after the re-chunk')
  await page.reload()
  await expect(page.getByTestId('topic-covered')).toHaveCount(1)
  await shot(page, '5-removed')
})

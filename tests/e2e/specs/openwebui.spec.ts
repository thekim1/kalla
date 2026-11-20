import { execFileSync } from 'node:child_process'
import { randomBytes } from 'node:crypto'
import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { apiUrl, devClientKey, eventually, fixtureSiteUrl, visibleSoon } from '../helpers'

// Open WebUI at the pinned tag against rag-api (to-verify 47, 50, 134 and 165; chat design §3.1, §4.2–§4.3, §9.3, §11.4):
// the assistant's description in the model selector, citation chips and their links, Open WebUI's message ids on every
// completion and regeneration, no task request (title, tags, follow-ups) reaching a model, and Stop closing rag-api's
// stream. Runs on the E2E stack after the pilot journey, which publishes the assistants it chats with; the stack's Open
// WebUI has local accounts, so the spec signs up one of its own. rag-api's record is read from its database through the
// Postgres container (its password stays inside the container). Stop needs an answer that streams for several seconds:
// start the stack with FakeGateway__Latency__TokenIntervalMs=80 and FakeGateway__Latency__AnswerWords=150 (about 12 s).

const openWebUiUrl = process.env.OPENWEBUI_URL ?? 'http://localhost:7410'
/** The seeded Open WebUI (dev) client (DevSeeder.DevClientId), whose key Open WebUI uses. */
const devClientId = '01929b6e-0000-7000-8000-0000000c11e7'
/** Open WebUI's DEFAULT_MODEL_METADATA in the AppHost (chat design §4.3). */
const defaultCapabilities = { builtin_tools: false }

type RagModel = { id: string; name: string; info?: { meta?: { description?: string } } }
type OwuiMessage = { id: string; role: string; parentId: string | null; done?: boolean; content: string }

/** rag-api's database, as `psql -At` rows split on `|`, run in the stack's Postgres container (the AppHost's `postgres-…`). */
function ragSql(sql: string): string[][] {
  const container = execFileSync('docker', ['ps', '--filter', 'name=^postgres-', '--filter', 'label=com.microsoft.developer.usvc-dev.build', '--format', '{{.Names}}'],
    { encoding: 'utf8' }).trim().split('\n')[0]
  if (!container) throw new Error('No AppHost Postgres container runs: start the E2E stack.')
  const output = execFileSync('docker', ['exec', '-i', container, 'sh', '-c', 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d ragdb -At -v ON_ERROR_STOP=1'],
    { input: sql, encoding: 'utf8' })
  return output.split('\n').filter(line => line.length > 0).map(line => line.split('|'))
}

/** The dev client's turns (AssistantDailyTotals.Messages) and answer calls to the gateway (AssistantDailyUsage, step chat-answer). */
function clientCounters(): { turns: number; answerCalls: number } {
  const [[turns, answerCalls]] = ragSql(`
    SELECT (SELECT coalesce(sum("Messages"), 0) FROM chat."AssistantDailyTotals" WHERE "ClientId" = '${devClientId}'),
           (SELECT coalesce(sum("Calls"), 0) FROM chat."AssistantDailyUsage" WHERE "ClientId" = '${devClientId}' AND "Step" = 'chat-answer');`)
  return { turns: Number(turns), answerCalls: Number(answerCalls) }
}

/** rag-api's messages of an Open WebUI chat: role, Open WebUI's message id and outcome. */
function ragMessages(chatId: string): { role: string; external: string; outcome: string }[] {
  expect(chatId).toMatch(/^[0-9a-f-]{36}$/)
  return ragSql(`
    SELECT m."Role", coalesce(m."ExternalMessageId", ''), coalesce(m."Outcome", '')
    FROM chat."Messages" m JOIN chat."Conversations" c ON c."Id" = m."ConversationId"
    WHERE c."ExternalChatId" = '${chatId}' ORDER BY m."Seq";`).map(([role, external, outcome]) => ({ role, external, outcome }))
}

const newAccount = () => ({ name: 'E2E Open WebUI', email: `e2e-owui-${randomBytes(6).toString('hex')}@example.invalid`, password: randomBytes(24).toString('base64url') })

/**
 * Signs up a local Open WebUI account with a random password and returns its token. The first account of an empty store
 * becomes Open WebUI's admin and switches sign-up off until a restart (routers/auths.py:831-834), so that admin then adds a
 * second account: the chats run as a `user` (DEFAULT_USER_ROLE), as the pilot's do.
 */
async function signUp(request: APIRequestContext): Promise<string> {
  const response = await request.post(`${openWebUiUrl}/api/v1/auths/signup`, { data: newAccount() })
  expect(response.status(), 'Open WebUI sign-up (ENABLE_SIGNUP; restart Open WebUI if its first account was made since it started)').toBe(200)
  const account = await response.json()
  if (account.role === 'user') return account.token as string

  const user = newAccount()
  const added = await request.post(`${openWebUiUrl}/api/v1/auths/add`, { data: { ...user, role: 'user' }, headers: { Authorization: `Bearer ${account.token}` } })
  expect(added.status()).toBe(200)
  const signIn = await request.post(`${openWebUiUrl}/api/v1/auths/signin`, { data: { email: user.email, password: user.password } })
  expect(signIn.status()).toBe(200)
  return (await signIn.json()).token as string
}

async function owuiChat(request: APIRequestContext, token: string, chatId: string): Promise<{ title: string; messages: OwuiMessage[] }> {
  const response = await request.get(`${openWebUiUrl}/api/v1/chats/${chatId}`, { headers: { Authorization: `Bearer ${token}` } })
  expect(response.status()).toBe(200)
  const chat = (await response.json()).chat
  return { title: chat.title, messages: Object.values(chat.history.messages) as OwuiMessage[] }
}

async function openChat(page: Page, token: string, model: string): Promise<void> {
  await page.addInitScript(value => localStorage.setItem('token', value), token)
  await page.goto(`${openWebUiUrl}/?model=${encodeURIComponent(model)}`)
  // A new account first sees the release notes.
  const notes = page.getByRole('button', { name: 'Okay, Let\'s Go!' })
  if (await visibleSoon(notes)) await notes.click()
  await expect(page.locator('#chat-input')).toBeVisible()
}

async function ask(page: Page, question: string): Promise<void> {
  await page.locator('#chat-input').fill(question)
  await page.locator('#chat-input').press('Enter')
}

let token: string
let assistant: RagModel

test.describe.configure({ mode: 'serial' })

test.beforeAll(async ({ request }) => {
  const models = await request.get(`${apiUrl}/v1/models`, { headers: { Authorization: `Bearer ${devClientKey()}` } })
  expect(models.status()).toBe(200)
  const listed = (await models.json()).data as RagModel[]
  // The dev seed's Demo-assistent (DemoSeeder) answers from uploads; the checks below need the pilot's, which cites the fixture website.
  const pilot = listed.filter(m => m.id !== 'demo-assistent')
  expect(pilot.length, 'rag-api lists the pilot\'s assistant to the dev client: run the pilot project first').toBeGreaterThan(0)
  assistant = pilot[0]
  token = await signUp(request)
})

test('165: the assistant\'s info.meta.description survives the model merge and shows in the model selector', async ({ page, request }) => {
  const description = assistant.info?.meta?.description
  expect(description, 'rag-api sends info.meta.description (chat design §3.1)').toBeTruthy()

  // Open WebUI's merged model list: our description as sent, and the capabilities exactly the default metadata's.
  const response = await request.get(`${openWebUiUrl}/api/models`, { headers: { Authorization: `Bearer ${token}` } })
  expect(response.status()).toBe(200)
  const merged = ((await response.json()).data as any[]).find(model => model.id === assistant.id)
  expect(merged, 'Open WebUI lists the assistant').toBeTruthy()
  expect(merged.name).toBe(assistant.name)
  expect(merged.info.meta.description).toBe(description)
  expect(merged.info.meta.capabilities).toEqual(defaultCapabilities)

  // The selector's entry is described by it (ModelItem.svelte: aria-describedby, the info icon's tooltip).
  await openChat(page, token, assistant.id)
  await page.getByRole('button', { name: `Selected model: ${assistant.name}` }).click()
  const option = page.getByRole('option', { name: `Select ${assistant.name} model` })
  await expect(option).toBeVisible()
  await expect(option).toHaveAccessibleDescription(description!)
})

test('47, 50, 134: citation chips and links, message ids on every completion and regeneration, no task request, Stop', async ({ page, request }) => {
  const before = clientCounters()
  await openChat(page, token, assistant.id)

  // Turn 1, a new chat. The question's sources include the three-page PDF (text-layer.pdf, page 2).
  const first = 'Inom hur lång tid ska ett överklagande av ett bygglovsbeslut ha kommit in till kommunen?'
  await ask(page, first)
  await page.waitForURL(/\/c\/[0-9a-f-]{36}$/, { timeout: 60_000 })
  const chatId = page.url().split('/c/')[1]
  const answered = async (count: number) => (await owuiChat(request, token, chatId)).messages.filter(m => m.role === 'assistant' && m.done).length >= count
  await eventually(() => answered(1), 'Open WebUI finishes the first answer', 90_000)

  await test.step('47: a chip per cited source, named by its label, with the link and the text-fragment link', async () => {
    const chips = page.locator('button[aria-label^="View source: "]')
    await expect(chips.first()).toBeVisible()
    const links: { name: string; link: string; content: string }[] = []
    for (let i = 0; i < await chips.count(); i++) {
      const chip = chips.nth(i)
      // The chip shows metadata.name (ContentRenderer.svelte:122-123).
      const name = (await chip.getAttribute('aria-label'))!.replace('View source: ', '')
      await chip.click()
      const modal = page.getByRole('dialog')
      const header = modal.locator('a[target="_blank"]').first()
      const link = (await header.getAttribute('href'))!
      // metadata.external keeps the label as the source's name; without it an http(s) source is named by its URL (Citations.svelte:127).
      await expect(header).toHaveText(name)
      const content = (await modal.getByRole('link', { name: 'Content', exact: true }).getAttribute('href'))!
      links.push({ name, link, content })
      await modal.getByRole('button', { name: 'Close citation modal' }).click()
      await expect(modal).toBeHidden()
    }

    console.log(`47: chips and links: ${JSON.stringify(links)}`)
    for (const { link, content } of links) {
      expect(link.startsWith(`${fixtureSiteUrl}/webbplats/`)).toBe(true)
      // CitationModal.svelte:68-98: the snippet link is the source link plus #:~:text=<first>,<last>.
      expect(content.startsWith(`${link}#:~:text=`)).toBe(true)
    }

    // A PDF's link carries its first cited page (chat design §9.3), so its snippet link has two fragments.
    const pdf = links.find(l => l.link.includes('.pdf'))
    expect(pdf, 'a PDF among the cited sources').toBeTruthy()
    expect(pdf!.link).toMatch(/\.pdf#page=\d+$/)
    expect(pdf!.content).toMatch(/\.pdf#page=\d+#:~:text=/)
    // The text before the chips holds no raw markers: each [n] became a chip.
    await expect(page.getByText(/\[\d+\]/)).toHaveCount(0)
  })

  // Open WebUI's background tasks of turn 1 (follow-ups, then the title, then tags; middleware.py:3597-3720) have run once
  // the chat has its title: with title generation off it is the first question.
  await eventually(async () => (await owuiChat(request, token, chatId)).title === first, 'Open WebUI titles the chat with the question', 60_000)

  // Turn 2, a regeneration of the first answer.
  await page.getByRole('button', { name: 'Regenerate' }).last().click()
  await page.getByRole('menuitem', { name: 'Try Again' }).click()
  await eventually(() => answered(2), 'Open WebUI finishes the regenerated answer', 90_000)

  // Turn 3, a follow-up question, stopped while the answer streams.
  const third = 'Vem prövar överklagandet?'
  await ask(page, third)
  const answerTo = (messages: OwuiMessage[], question: string) =>
    messages.find(m => m.role === 'assistant' && messages.find(q => q.id === m.parentId)?.content === question)
  await eventually(async () => answerTo((await owuiChat(request, token, chatId)).messages, third) !== undefined, 'Open WebUI saves the third turn', 30_000)
  const stopped = answerTo((await owuiChat(request, token, chatId)).messages, third)!
  // The FakeGateway's answer streams one word per FakeGateway:Latency:TokenIntervalMs; Stop once its first words show.
  await expect(page.locator(`[id="message-${stopped.id}"] #response-content-container`)).toContainText('Testsvar på', { timeout: 60_000 })
  await page.getByRole('button', { name: 'Stop', exact: true }).click()

  const owui = (await owuiChat(request, token, chatId)).messages
  const owuiAnswers = owui.filter(m => m.role === 'assistant')
  const owuiQuestions = owui.filter(m => m.role === 'user')
  expect(owuiAnswers).toHaveLength(3)
  expect(owuiQuestions).toHaveLength(2)

  // rag-api's record of the chat, once the cancelled turn is written (the writer flushes in the background).
  await eventually(async () => ragMessages(chatId).some(m => m.external === stopped.id && m.outcome !== ''), 'rag-api records the stopped turn', 60_000)
  const rag = ragMessages(chatId)
  console.log(`50/47: rag-api messages: ${JSON.stringify(rag)}`)

  await test.step('50: {{MESSAGE_ID}} reached rag-api on both completions and the regeneration', async () => {
    const ragAnswers = rag.filter(m => m.role === 'assistant')
    expect(ragAnswers.map(m => m.external).sort()).toEqual(owuiAnswers.map(m => m.id).sort())
    // {{USER_MESSAGE_ID}} too, and the regeneration's user message is not stored twice (chat design §11.4).
    const ragQuestions = rag.filter(m => m.role === 'user')
    expect(ragQuestions.map(m => m.external).sort()).toEqual(owuiQuestions.map(m => m.id).sort())
  })

  await test.step('47: Stop closed the stream to rag-api, which recorded the turn as cancelled', async () => {
    expect(rag.find(m => m.external === stopped.id)!.outcome).toBe('cancelled')
  })

  await test.step('134: rag-api received three turns and made three answer calls, so no task request reached a model', async () => {
    const after = clientCounters()
    console.log(`134: turns ${before.turns} → ${after.turns}, answer calls ${before.answerCalls} → ${after.answerCalls}`)
    expect(after.turns - before.turns).toBe(3)
    expect(after.answerCalls - before.answerCalls).toBe(3)
  })
})

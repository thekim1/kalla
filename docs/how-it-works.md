# How it fits together

Ume.Rag has four kinds of things to set up: **knowledge bases** hold the content, **assistants** answer from them,
**apps** (with their **keys**) are the programs that may use the assistants, and **sign-in** decides who may change any
of it in the admin UI. This page explains each one and how a request is checked. The admin UI uses the same names:
*Knowledge bases*, *Assistants* and *Apps and keys*.

## Departments own the content

Departments and their teams come from the AI gateway's organisation. rag-admin reads them every 5 minutes, and
*Organisation → Fetch now* reads them at once. Every knowledge base and every assistant belongs to one department,
with an optional owner team. A department's admins see and change only their own department's objects, and an object
of another department answers *not found*, as if it did not exist. Platform admins see every department.

## Information classes

Every knowledge base, source, assistant and app has an **information class**: `public`, `internal` or `sensitive`. The
class decides two things:

- **Where models may run.** Every model call goes through the gateway with a residency ceiling, and the gateway refuses
  any route further out. `sensitive` content only uses on-prem models, `internal` content on-prem or EU-hosted models,
  and `public` content any model the gateway offers.
- **Who may see it.** An app has a ceiling, and it never reaches content above it. An assistant's class must cover
  every knowledge base it links: an `internal` assistant may link `public` and `internal` knowledge bases, never a
  `sensitive` one.

## Knowledge bases

A knowledge base is the content an assistant searches. It holds one or more **sources**:

| Source | What it fetches |
|---|---|
| **Website** | Crawls from a start address within the limits you set (pages, depth, include and exclude rules), with robots.txt and sitemaps. Pages that need JavaScript go through a headless browser. |
| **Address list** | Exactly the addresses listed. |
| **Files** | Uploaded files: PDF, Office and OpenDocument files, images and scans. Every file is virus-scanned before anything reads it. |

Each source runs on a schedule or on *Fetch now*. A fetch parses each document, splits it into chunks, looks for
personal data and embeds the chunks for search. A document that disappears from a website is proposed for deletion
first: when a fetch would remove more than the deletion guard allows, an admin confirms or rejects it.

- **Publishing.** A new knowledge base is only searchable in the test panel until it is published. Its test questions
  measure how well it finds the right documents, and the nightly evaluation warns when that gets worse.
- **Topics.** A source's path rules tag chunks with topics (for example `bygglov`). An assistant can link a knowledge
  base whole, or only for some topics.
- **Sharing.** A knowledge base is shared with *only our department* (the default), *selected departments* or *the
  whole municipality*. Another department may link it from its own assistants. It then sees the knowledge base's name,
  owner, class, contact and topics, never its sources, documents or settings.
- **Deleting.** A source can be deleted at any time: its documents leave the search at once and are removed for good
  after 14 days. A knowledge base can be deleted once no assistant (active or archived) links it and it has no
  sources left. Its name is free for a new one at once. Nothing deleted can be restored.

## Assistants

An assistant is what an app's users talk to. To an app it is a **model**: its model name (the slug, for example
`demo-assistent`) is what `GET /v1/models` lists and what `POST /v1/chat/completions` names.

- **What it answers with** lives in a **version**: the instructions, the chat model, and the knowledge bases it links
  (each whole or for some topics). You edit a **draft**, try it in the test panel and **publish** it. The published
  version is what apps get. An earlier version can be restored, which publishes a copy of it.
- **How it answers:** from the linked knowledge bases only, with numbered sources and each document's date. When the
  sources hold no answer, it says so and gives the assistant's contact links instead of guessing. General knowledge is
  off unless an admin turns it on for the assistant.
- **Who can use it** is the list of apps it is offered to. An app sees only the assistants offered to it.
- **Its gateway key** decides whose budget its model calls are charged to (*Gateway keys and costs*, below).
- **Archive** hides it from every app and keeps everything, so it can come back. **Delete** removes it for good: no
  app lists or reaches it from that moment, and its name is free for a new assistant at once. Its conversations and
  statistics stay for their own retention time. An app that keeps its own chats or settings under the model name, such
  as Open WebUI, keeps them for a new assistant with the same name.

## Apps and keys

An **app** (a client) is a program that calls Ume.Rag's API. Platform admins add apps under *Apps and keys*:

| Kind | Use |
|---|---|
| **Open WebUI** | The chat for staff. Open WebUI calls Ume.Rag with one key and forwards who the user is. |
| **Integration** | Another system that calls the API with its own key, for example a website's search. |
| **Internal** | The platform's own tools, which may see every information class. |

The kinds *website widget*, *own chat client*, *Teams*, *MCP client* and *Entra app* are shown as coming later and
cannot be created yet.

Each app has an information class ceiling and says whether it keeps chat history. An app that keeps history cannot be
`sensitive`.

**Keys** belong to an app:

- A key looks like `ume-rag-sk-` followed by 43 characters. It is shown once when it is created. Ume.Rag stores only a
  keyed hash of it, so a lost key cannot be read back from the database; platform admins can reveal it in the admin UI,
  and every reveal is in the audit log. The admin UI shows its first characters (`ume-rag-sk-AbC1`) to tell keys apart.
- A key can be limited to some assistants or knowledge bases, to IP ranges and to a number of requests per minute,
  and it can expire. A department admin manages the keys of their department's teams.
- **Rotate** makes a new key with the same limits, and the old one stops at once or after 24 hours. **Revoke** stops
  it within seconds.
- Gateway keys (`ume-sk-…`) and Ume.Rag keys are separate: neither system accepts the other's.

The app sends its key as `Authorization: Bearer <key>`, or in `x-api-key`. What a key reaches:

- **Chat** (`GET /v1/models`, `POST /v1/chat/completions`): the published, active assistants offered to the key's
  app, within the app's ceiling and the key's limits.
- **Search** (`POST /v1/retrieve`, `GET /v1/collections`, the document routes): only apps of the kind **Internal** by
  default (`Knowledge:OpenToClientKinds`).
- **Open WebUI** sends the user's identity in a signed header (`X-OpenWebUI-User-Jwt`), checked only for the
  deployment's own Open WebUI app. Without that header, the key lists models but cannot chat.

A request outside what the key may reach answers *not found* (`404`, or `model_not_found` for chat), whatever the
reason, so a caller cannot find out which assistants exist outside its reach. The real reason goes to the security
log ([security events](security-events.md)).

## Gateway keys and costs

Ume.Rag never calls a model directly. Each service calls the gateway with its own **credential key**. A knowledge base
or an assistant may also have a **gateway key** of its own, picked from the keys the gateway has assigned to Ume.Rag.
Its calls (embedding for a knowledge base, answering for an assistant) are then charged to that key, its team and its
department, and the gateway enforces their budgets. Without one, the calls are charged to the platform's own team.

When an assistant's gateway key is disabled or its budget is used up, the assistant answers that it is unavailable and
its page in the admin UI says why. The runbook's *Gateway keys, budgets and the read-back* has every case and its fix,
and *Costs* in the admin UI shows what each assistant and knowledge base cost.

## Signing in to the admin UI

The admin UI signs in with OIDC: Keycloak (on its own, or the gateway's shared realm), Entra ID or AD FS. A new
installation sets the provider in the **setup wizard** ([Keycloak](keycloak.md); the runbook's *Sign-in and the setup
wizard*). Chat users never sign in to Ume.Rag: they sign in to their app, such as Open WebUI, and the app vouches for
them.

What a person may do comes from their roles, taken from the provider's role claim or from groups mapped under
*Settings → Role groups*:

| Role | May |
|---|---|
| `rag-platform-admin` | Everything, in every department, and the platform's own settings, apps and models |
| `rag-department-admin` | Manage their departments: publish, share, delete, set who can use an assistant, keys of their teams, plus everything an editor may do |
| `rag-editor` | Create and edit knowledge bases, sources and assistant drafts, test, and publish assistants |
| `rag-pipeline-editor` | Edit processing pipelines |
| `rag-reviewer` | Review conversations and feedback (nothing to review while chat content is not stored) |
| `rag-viewer` | Read only |

A person's departments come from the `departmentCodes` claim, or from the groups they belong to that a department
lists as its member groups. Someone without any `rag-*` role sees a *no access* page.

## Where the data is stored

Everything stays on the installation's own host: one PostgreSQL database and one file store. Ume.Rag sends nothing
anywhere else except model calls through the gateway (the text of the chunks to embed, a question with its sources to
answer) and, when configured, telemetry to your collector.

| Where | Docker volume | What |
|---|---|---|
| **PostgreSQL** (`rag-postgres`, with pgvector) | `rag-postgres-data` | Four schemas. `config`: knowledge bases, sources, assistants, apps and keys (as hashes), settings. `content`: each document's metadata, its chunks with their text, search vectors and embeddings, runs, the job queue and the test questions. `chat`: conversation records and statistics. `audit`: the audit log. |
| **File store** (a directory, `/data/blobs/rag` in the containers) | `rag-blobs` | `raw/`: pages and files as fetched. `parse/` and `stages/`: the parser's and the pipeline's outputs. `uploads/`: uploaded files. `quarantine/`: files the virus scan stopped. Short-lived previews and temporary files. |
| **Open WebUI** (optional) | `rag-openwebui-data` | Open WebUI's own database, with its copies of the chats (below). |

**How it is protected.**
- The volumes must be on an encrypted disk ([production setup](production-setup.md)).
- In the file store, everything from `internal` sources is also encrypted on its own: each file has its own key,
  wrapped by the master key `rag-blob-internal`, which is kept with the other long-lived keys (the runbook's *Key
  escrow*). `public` files rely on the encrypted disk.
- Files are written once and never changed in place. Several documents with the same content share one file.
- In the database, only the services' own database roles reach the data, each with the rights it needs.

### What a crawl or upload keeps

For every page or file a source brings in, Ume.Rag keeps:
- the original as fetched or uploaded;
- the document's address or file name, title, dates (modified, fetched, last seen) and a hash of its content;
- the parsed text, split into chunks, with their embeddings;
- for website sources, the links found on each page;
- a report of each fetch: what was added, changed, removed or failed.

A personal-data check records only which types it found and how many, never the values.

| Data | Kept |
|---|---|
| Documents and chunks | While the source exists. A page that disappears from its website leaves the search at the next fetch. |
| The previous version of a document's chunks | 14 days, so a publication can be rolled back |
| A deleted source's documents | Out of the search at once, removed for good after 14 days; their files after the blob purge delay (35 days, the backup retention) |
| Parser and pipeline outputs | 30 days after their last use, then made again when needed |
| Quarantined files | 30 days |
| Fetch reports | 13 months, and always the source's latest complete fetch; the per-document lines of a fetch 90 days |

**Backups.**
- The database is backed up every night with pgBackRest, and its write-ahead log continuously. The backups are
  encrypted before they leave the host and are kept 35 days on the backup target agreed with IT.
- Uploaded files are copied to the same target every 10 minutes.
- Fetched pages and parser outputs are not backed up: after a restore, the next fetch brings them back.

### What is kept of chats

Today Ume.Rag stores **no question and no answer text**. For each conversation and message it keeps:
- which assistant and app it was, and the user's id in that app (for Open WebUI, its user id);
- the times, the outcome (answered, not found, unavailable) and the ids of the documents it cited;
- the tokens and the cost.

A rating keeps the thumbs and the score, and the reason only as one of the fixed categories. These records are deleted
after the assistant's retention time (90 days by default). The daily statistics per assistant (counts, tokens, cost)
are kept 3 years.

**Open WebUI keeps its own copies.** It stores every chat in its own database. Ume.Rag does not delete those copies
and no backup script includes them, so Open WebUI's own settings decide how long they stay.

### Logs and the audit log

| What | Where | Kept |
|---|---|---|
| **Service logs** (rag-api, rag-admin, rag-worker and the others) | JSON on each container's standard output: `docker compose logs <service>`. With `UME_OTEL_ENDPOINT` set, also sent with metrics and traces to your OpenTelemetry collector ([production setup](production-setup.md), *Telemetry*); traces are sampled, a tenth of the requests by default. | As Docker's log driver keeps them on the host (set rotation in `/etc/docker/daemon.json`), or as the collector's backend keeps them |
| **Security events** (refused keys and sign-ins, access denials, malware and personal-data findings, admin changes) | Log records under the category `Ume.Rag.Security`, which the collector sends on to the SIEM ([security events](security-events.md)) | As the SIEM keeps them |
| **Audit log** (every change made in the admin UI or the admin API, with who made it and the values before and after) | The database, schema `audit`; *Audit log* in the admin UI | 2 years by default (`Settings.AuditRetentionDays`, at least 90 days) |
| **Backup metrics** | Prometheus text files in `/var/lib/ume-rag/` on the host | Overwritten at each run |

No log, event or audit entry ever holds key material, tokens, passwords, questions, answers, document text, e-mail
addresses or display names. They name things by id instead: the key's id and prefix, the app's id, the user's id in
their app, and the admin's id at the sign-in provider.

## MCP

Ume.Rag does **not** serve MCP yet: there is no `/mcp` endpoint and no MCP tools. The *MCP client* app kind is
reserved for it. It is planned together with skills and tools: an `/mcp` endpoint with a `search_knowledge` tool, for
MCP clients signing in with their own tokens. Until then, other programs use the knowledge API (`POST /v1/retrieve`)
or the chat API with a key.

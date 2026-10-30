# Architecture diagrams

Mermaid views of the Ume.Rag design as it stands after the simplification pass
([`simplification-pass.md`](simplification-pass.md)) and the P1 split of the
[weakness review §1](weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer). Sources: networks and containers
[`platform/repo-and-deployment.md`](platform/repo-and-deployment.md) §6, data ownership
[`platform/data-model.md`](platform/data-model.md), request paths [`chat-api/design.md`](chat-api/design.md) §4,
[`knowledge-api/design.md`](knowledge-api/design.md) §6, ingestion
[`ingestion-worker/design-engine.md`](ingestion-worker/design-engine.md), tools [`tools/design.md`](tools/design.md) §2.
Where a diagram and a document differ, the document wins.

## 1. System overview

Dashed edges and boxes marked P2 are not in the pilot. The pilot runs six long-running containers (rag-api, rag-admin,
rag-worker, rag-parser, rag-clamav, postgres) plus the one-shot rag-migrate; Redis arrives with the first multi-host
deployment (P2).

```mermaid
flowchart LR
    subgraph clients["Clients"]
        owui["Open WebUI<br/>(pilot client; own VM from P2)"]
        browser["Admin browser"]
        mcpc["MCP clients (P2)"]
        integ["Integrations / widget (P2)"]
    end

    proxy["Municipal reverse proxy<br/>(TLS)"]

    subgraph data_plane["Data plane zone"]
        api["rag-api (one slot in the pilot; a/b from P2)<br/>roles: chat, knowledge, mcp (P2)<br/>Ume.Rag.Answering + Ume.Rag.Retrieval<br/>in-process tool hub (P2)"]
    end

    subgraph control_plane["Control plane zone"]
        admin["rag-admin<br/>BFF + Swedish journey pages<br/>test panel, config writer<br/>assistant drafts, versions<br/>test questions, nightly answer judge,<br/>skill gate, in-process tool hub (P2)"]
    end

    subgraph ingestion_zone["Ingestion zone (rag-ingestion.slice)"]
        worker["rag-worker<br/>connectors, fixed pipelines,<br/>site profiles, retrieval evaluator, index writer<br/>(no tools, not on network mcp)"]
        parser["rag-parser<br/>every PDF: pypdfium2, Docling, Tesseract swe<br/>Office, images, gated NER"]
        renderer["rag-renderer (P2)<br/>headless browser, only if a source needs JavaScript"]
        clamav["rag-clamav"]
    end

    subgraph data_zone["Data zone"]
        pg[("Postgres: pgvector image + pgBackRest<br/>config | content | chat | audit<br/>admin sessions, Data Protection keys")]
        redis[("Redis (P2, multi-host only)")]
        blobs[("Blob store, write-once<br/>internal: envelope-encrypted; public: encrypted disk<br/>file system (pilot); S3 object store beyond one host")]
    end

    backup[("Backup target<br/>pgBackRest WAL archive + PITR,<br/>upload copies, key escrow offline")]

    subgraph hosted["Hosted MCP servers (P2)"]
        mcpsrv["ume-mcp-kommun<br/>(mock fee calculator first)"]
    end

    gateway["LLM gateway (separate repo and database)<br/>one combined release: residency ceiling,<br/>delegation, member groups from claims<br/>chat, embeddings BGE-M3"]
    idp["Entra ID / Keycloak"]
    web["Municipal and external websites"]
    clamdb["database.clamav.net"]

    owui -- "OpenAI API + signed user JWT" --> proxy
    browser --> proxy
    mcpc -. "MCP, Entra token" .-> proxy
    integ -. "keys" .-> proxy
    proxy --> api
    proxy --> admin

    api -- "chat, condense, query embeddings<br/>key per level: OnPrem, Eu, open<br/>x-ume-on-behalf-of, explicit x-ume-max-residency" --> gateway
    admin -- "test panel, Data API sync,<br/>/v1/models at the level's ceiling,<br/>answer judge, skill gate (P2)" --> gateway
    worker -- "embeddings, LLM stages<br/>OnPrem and Eu keys only, explicit ceiling" --> gateway

    api --> pg
    api -. "shared rate limits, L2 query-embedding cache (P2)" .-> redis
    api -- "download grants (read-only)" --> blobs
    admin -- "config + outbox (serialised writes), jobs" --> pg
    worker -- "jobs, documents, chunk sets" --> pg
    worker --> blobs
    worker -. "crawl and embedding in-flight leases (P2, multi-host)" .-> redis

    pg -. "LISTEN/NOTIFY: config version, jobs" .-> api
    pg -. "LISTEN/NOTIFY" .-> worker
    pg -- "pgBackRest archive-push (archive_timeout 60 s),<br/>backups from a host timer" --> backup
    blobs -- "uploads within 15 min" --> backup

    worker -- "multipart parse, /v1/inspect,<br/>gated /v1/ner" --> parser
    worker -. "JavaScript sources only (P2)" .-> renderer
    worker -- "scan files" --> clamav
    clamav --> clamdb
    worker -- "crawl network, egress guard" --> web

    api -. "MCP Streamable HTTP via the in-process hub (P2)" .-> mcpsrv
    admin -. "discovery, test panel, skill gate (P2)" .-> mcpsrv

    api -- "feedback import (HTTP API)" --> owui
    admin -- "OIDC sign-in" --> idp
    api -. "JWKS (P2)" .-> idp

    style redis stroke-dasharray: 5 5
```

The zones are one host in the pilot, with one slot per service and an announced maintenance window for updates. From
the first multi-host deployment (P2, [`performance-pass.md`](performance-pass.md) §5) the same images run per host by
role, Compose per host: three hosts in P2 (**D** data: Postgres, Redis, object store; **A** data plane and control;
**I** ingestion), five in P3 (D; **A1/A2** rag-api and rag-admin; **F** fetch: worker
`preview,command,sync,fetch,eval`, clamd, small parser, renderer if needed; **P** processing: worker `parse,embed` with
its own parser). A Postgres streaming standby is required before production beyond the pilot (register D1). Parser,
renderer and clamd stay host-local to their worker; all hosts share the one Postgres, Redis and object store over a
routed LAN with TLS. In the pilot, rate limits and the query-embedding cache are in process and admin sessions are in
Postgres. A separate `rag-tools` service (mTLS hop, own database role) comes only with the first external,
OBO or write tool (P3).

## 2. Chat request (RAG assistant, P1)

Every assistant without a tool-bearing skill takes this path; chat serves the assistant's published version, and
retrieval searches only the collections that version links directly (simplification pass §15).

```mermaid
sequenceDiagram
    autonumber
    participant U as Open WebUI
    participant C as rag-api chat role
    participant K as Knowledge core (Ume.Rag.Answering)
    participant P as Postgres
    participant G as LLM gateway
    participant W as Background writer

    U->>C: POST /v1/chat/completions (model = assistant, history, user JWT, chat id)
    C->>C: authenticate, published assistant version and IAccessPolicy at the confirmed config version (grace window 90 s)
    C->>C: effective level = max(assistant level, client ceiling), fixed from the first message
    C->>C: per-user rate limit (in process in the pilot, Redis from P2)
    opt follow-up question that is not self-contained (length, anaphora)
        C->>G: condense on Settings.Chat.CondenseModel (x-ume-step: condense, explicit ceiling)
        G-->>C: standalone query (timeout 600 ms or failure: raw question)
    end
    C->>K: RetrieveAsync(assistant, query, top_k 10, expand section) over the version's direct collections
    par keyword in parallel with the embedding
        K->>P: K: keyword candidates (decompounded lexemes, built-in swedish, top 100)
    and
        K->>K: query embedding cache (in process, HMAC key)
        K->>G: embed query on-prem on a miss
    end
    K->>P: V: vector candidates (exact scan or halfvec HNSW, CollectionId filter)
    K->>K: RRF fusion, dedup, cap 3 per document (P2: IReranker), coverage
    K->>P: H+D: hydrate hits, sections, documents, breadcrumbs (one batch)
    K-->>C: results, sections, citations, coverage
    alt coverage low (best distance above threshold and keyword list below its floor)
        C-->>U: fixed not-found text + contact links (no model call)
    else coverage ok or degraded, or no content words (gate skipped, no-sources frame)
        C->>G: stream answer (per-level key, x-ume-on-behalf-of, explicit x-ume-max-residency)
        G-->>C: headers: x-ume-residency, delegation echo (503 + Retry-After when targets are drained)
        C->>C: check residency and echo before the first byte
        G-->>C: SSE deltas
        C-->>U: filtered text, [n] renumbered, Open WebUI source events
    end
    C->>W: enqueue turn (encrypted under the bucket key of its level and expiry month)
    W->>P: one transaction per batch: messages, sources, cited ranks, daily totals
```

## 3. Ingestion and publishing

```mermaid
flowchart TD
    subgraph admin_side["Admin (rag-admin)"]
        form["Source form + template parameters<br/>preflight, preview with site-profile before/after"]
        publish["Publish: re-process every document (P1),<br/>plan from changed parameters (P2)"]
        decide{"Owner: keep or roll back<br/>(previous chunk sets kept 14 days)"}
        cfg[("config tables<br/>+ outbox, version bump")]
        judge["Nightly answer judge (P2)<br/>Ume.Rag.Answering + on-prem judge"]
    end

    subgraph worker_side["Worker (rag-worker): one fixed pipeline per source type"]
        sched["Scheduler / EnsureCurrent"]
        sync["sync job: sitemap lastmod (incremental once a 500-URL sample<br/>shows it reliable; full traversals until then),<br/>change detection, deletion guard (complete at ≥ 99 % seeds),<br/>removal check, alert after 3 runs without a full traversal"]
        profile["Site profile per host<br/>P1: hand-checked Sitevision preset (manual JSON),<br/>P2: learned from 30–50 sample pages;<br/>versioned, rollback"]
        fetch["fetch job: final URL identity (verified profile rule),<br/>content hash, ClamAV for files"]
        parse["parse: HTML, text, Markdown, CSV in the worker<br/>every PDF, Office file and image to the parser<br/>(cache: content hash, settings, build)"]
        proc["clean (profile: root, boilerplate, breadcrumb, date),<br/>split by headings, chunk ~400 tokens"]
        pii["personal data: patterns<br/>+ gated name check (/v1/ner) in processing"]
        embed["embed job: BGE-M3 on-prem via gateway<br/>(cache: text hash, profile)"]
        index["index writer: new chunk set written with DeletedAt set,<br/>batches of 1,000, short swap per document"]
        evalj["eval job (advisory, retrieval only): full collection,<br/>recall@10 and MRR vs the previous run"]
    end

    jobs[("content.Jobs<br/>LISTEN/NOTIFY")]
    store[("Documents and chunk sets per source<br/>Chunks with scalar CollectionId")]
    report["Evaluation report<br/>on the publish screen"]
    parserp["rag-parser<br/>/v1/inspect routes: pypdfium2 text layer,<br/>Docling layout, Tesseract OCR"]
    gw["LLM gateway"]

    form --> cfg
    cfg --> sched
    publish -- "reprocess run" --> jobs
    sched --> jobs
    jobs --> sync --> fetch --> parse --> proc --> pii --> embed --> index
    fetch -. "sample pages (first run, re-learn; P2)" .-> profile
    profile -. "HTML stage parameters" .-> proc
    parse -. "PDF, Office, images" .-> parserp
    pii -. "names, gated" .-> parserp
    embed --> gw
    judge -- "answers, faithfulness and citation precision" --> gw
    index --> store
    index -- "after publish, nightly" --> evalj
    evalj --> report
    report --> decide
    decide -. "roll back: the same swap back" .-> store
    store -- "live chunk sets of published collections" --> retrieval["Knowledge API retrieval"]
```

## 4. Tool call from an agent assistant (P2)

An assistant runs this loop when its published version links a skill with tools; the mode is derived from its skills,
not chosen (simplification pass §15). The tool hub runs in-process in the chat role (`IToolHub`), and in `rag-admin`
for discovery, the test panel and the skill gate; never in the worker. Skills totalling ≤ about 4k tokens are included statically
with their tools visible from step 1, and `search_knowledge` is prefetched with the user question.

```mermaid
sequenceDiagram
    autonumber
    participant M as Agent loop (Ume.Rag.Answering, chat role)
    participant G as LLM gateway
    participant H as Tool hub (in-process, chat role)
    participant S as Hosted MCP server

    M->>G: step n with tools = ToolPolicy.Visible (explicit x-ume-max-residency)
    G-->>M: tool_calls (streamed deltas)
    M->>M: ToolPolicy.Authorize (same function), lethal-trifecta check (hard refusal), limits 8 calls / 60 s / $0.05
    M->>H: IToolHub call (pinned definition id)
    H->>H: bulkhead per server (concurrency, timeout, circuit), schema validation, rate limits
    H->>S: tools/call (credential from resolver)
    S-->>H: result
    H->>H: normalise, cap, label untrusted, audit (HMAC of arguments)
    H-->>M: tool_result
    M->>G: next step (or final answer, tool_choice none at the limit)
```

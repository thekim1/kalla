# Architecture diagrams

Mermaid views of the Ume.Rag design as it stands after the consistency pass. Sources: networks and containers
[`platform/repo-and-deployment.md`](platform/repo-and-deployment.md) §6, data ownership
[`platform/data-model.md`](platform/data-model.md), request paths [`chat-api/design.md`](chat-api/design.md) §4,
[`knowledge-api/design.md`](knowledge-api/design.md) §6, ingestion
[`ingestion-worker/design-engine.md`](ingestion-worker/design-engine.md), tools [`tools/design.md`](tools/design.md) §2.
Where a diagram and a document differ, the document wins.

## 1. System overview

Dashed boxes and edges marked P2 are not in the pilot.

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
        api["rag-api + rag-api-b<br/>roles: chat, knowledge, mcp<br/>Ume.Rag.Answering + Ume.Rag.Retrieval"]
        tools["rag-tools (P2)<br/>tool hub, only credential holder"]
    end

    subgraph control_plane["Control plane zone"]
        admin["rag-admin + rag-admin-b<br/>BFF + Swedish admin UI<br/>test panel, config writer"]
    end

    subgraph ingestion_zone["Ingestion zone (rag-ingestion.slice)"]
        worker["rag-worker<br/>connectors, pipeline engine,<br/>evaluator, index writer"]
        parser["rag-parser<br/>Docling, OCR, NER"]
        renderer["rag-renderer<br/>headless browser"]
        clamav["rag-clamav"]
    end

    subgraph data_zone["Data zone"]
        pg[("Postgres + pgvector + Swedish Hunspell<br/>config | content | chat | audit")]
        redis[("Redis")]
        blobs[("Blob store<br/>envelope-encrypted")]
    end

    subgraph hosted["Hosted MCP servers (P2)"]
        mcpsrv["ume-mcp-avgifter<br/>(mock fee calculator)"]
    end

    gateway["LLM gateway<br/>(separate repo and database)<br/>chat, embeddings BGE-M3, residency"]
    idp["Entra ID / Keycloak"]
    web["Municipal and external websites"]
    clamdb["database.clamav.net"]

    owui -- "OpenAI API + signed user JWT" --> proxy
    browser --> proxy
    mcpc -. "MCP, Entra token" .-> proxy
    integ -. "keys" .-> proxy
    proxy --> api
    proxy --> admin

    api -- "chat, embeddings<br/>x-ume-on-behalf-of, x-ume-max-residency" --> gateway
    admin -- "test panel, Data API sync" --> gateway
    worker -- "embeddings, LLM nodes" --> gateway

    api --> pg
    api --> redis
    api -- "download grants (read-only)" --> blobs
    admin -- "config + outbox, jobs" --> pg
    admin --> redis
    worker -- "jobs, documents, chunks" --> pg
    worker --> blobs

    pg -. "LISTEN/NOTIFY: config version, jobs" .-> api
    pg -. "LISTEN/NOTIFY" .-> worker

    worker -- "multipart parse, /v1/ner" --> parser
    worker -- "fetches every request" --> renderer
    worker -- "scan" --> clamav
    clamav --> clamdb
    worker -- "crawl network, egress guard" --> web

    api -. "mTLS + service key (P2)" .-> tools
    admin -. "discovery, test (P2)" .-> tools
    tools -. "MCP Streamable HTTP" .-> mcpsrv
    tools --> pg

    api -- "feedback import" --> owui
    admin -- "OIDC sign-in" --> idp
    api -- "JWKS (P2)" --> idp
```

## 2. Chat request (RAG assistant, P1)

```mermaid
sequenceDiagram
    autonumber
    participant U as Open WebUI
    participant C as rag-api chat role
    participant K as Knowledge core (Ume.Rag.Answering)
    participant R as Redis
    participant P as Postgres
    participant G as LLM gateway
    participant W as Background writer

    U->>C: POST /v1/chat/completions (model = assistant, history, user JWT, chat id)
    C->>C: authenticate, IAccessPolicy at confirmed config version (memory only)
    C->>R: per-user rate limit (1 command)
    opt follow-up question
        C->>G: condense on on-prem task model (x-ume-step: condense)
        G-->>C: standalone query (fallback: raw question)
    end
    C->>K: RetrieveAsync(assistant, query, top_k 10, expand section)
    par keyword in parallel with the embedding
        K->>P: K: Swedish full-text candidates (top 100)
    and
        K->>R: query embedding cache (public/internal)
        K->>G: embed query on-prem on a miss
    end
    K->>P: V: vector candidates (exact scan or HNSW)
    K->>K: RRF fusion, dedup, cap 3 per document, coverage
    K->>P: H+D: hydrate hits, sections, documents (one batch)
    K-->>C: results, sections, citations, coverage
    alt coverage low
        C-->>U: fixed not-found text + contact links (no model call)
    else coverage ok or degraded
        C->>G: stream answer (x-ume-on-behalf-of, x-ume-max-residency)
        G-->>C: headers: x-ume-residency, delegation echo
        C->>C: check residency and echo before the first byte
        G-->>C: SSE deltas
        C-->>U: filtered text, [n] renumbered, Open WebUI source events
    end
    C->>W: enqueue turn (encrypted per conversation, 7-day key epochs)
    W->>P: one transaction per batch: messages, sources, daily totals
```

## 3. Ingestion and publishing

```mermaid
flowchart TD
    subgraph admin_side["Admin (rag-admin)"]
        form["Source form + template<br/>preflight, preview"]
        publish["Publish: plan, gate, confirm<br/>(version state 'gating')"]
        cfg[("config tables<br/>+ outbox, version bump")]
    end

    subgraph worker_side["Worker (rag-worker)"]
        sched["Scheduler / EnsureCurrent"]
        sync["sync job: list sources,<br/>change detection, deletion guard"]
        fetch["fetch job: URL identity,<br/>render variant, ClamAV"]
        parse["parse job: auto-parse rules<br/>(worker or parser)"]
        proc["inline nodes: clean, split by headings,<br/>chunk ~400 tokens, metadata, quality"]
        ner["ner job: personal data<br/>(patterns + parser /v1/ner)"]
        embed["embed job: BGE-M3 on-prem<br/>via gateway"]
        index["index writer: chunk sets,<br/>collection membership"]
        evalj["eval job: evaluation sets,<br/>gates (recall@10)"]
    end

    jobs[("content.Jobs<br/>LISTEN/NOTIFY")]
    store[("Shared document store<br/>Documents, ChunkSets, Chunks")]
    parserp["rag-parser<br/>Docling, OCR"]
    gw["LLM gateway"]

    form --> cfg
    cfg --> sched
    publish --> jobs
    sched --> jobs
    jobs --> sync --> fetch --> parse --> proc --> ner --> embed --> index
    parse -. "hard formats" .-> parserp
    ner -. "names" .-> parserp
    embed --> gw
    index --> store
    jobs --> evalj
    evalj --> store
    evalj -- "gate result" --> publish
    store -- "published collections only" --> retrieval["Knowledge API retrieval"]
```

## 4. Tool call from an agent assistant (P2)

```mermaid
sequenceDiagram
    autonumber
    participant M as Agent loop (Ume.Rag.Answering, chat role)
    participant G as LLM gateway
    participant H as rag-tools (tool hub)
    participant S as MCP server

    M->>G: step n with tools = ToolPolicy.Visible (loaded skills only)
    G-->>M: tool_calls (streamed deltas)
    M->>M: ToolPolicy.Authorize (same function), limits 8 calls / 60 s / $0.05
    M->>H: POST /internal/v1/tool-calls (mTLS + service key, pinned definition id)
    H->>H: policy re-check, schema validation, rate limits
    H->>S: tools/call (credential from resolver)
    S-->>H: result
    H->>H: normalise, cap, label untrusted, audit (HMAC of arguments)
    H-->>M: tool_result
    M->>G: next step (or final answer, tool_choice none at the limit)
```

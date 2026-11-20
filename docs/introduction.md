# Introduction

Ume.Rag is the RAG, crawling and chat platform of Umeå kommun: it collects the municipality's knowledge from websites
and documents, keeps it current, and answers questions about it in chat with a cited source for every claim. It is
built as small .NET 10 services on top of the [Ume AI Gateway](https://github.com/thekim1/llmgateway), which every
model call goes through, with a Python parser and PostgreSQL with pgvector.

**Status:** the pilot (P1) is built and released; the live block (the real gateway, the shared sign-in, the umea.se
crawl, the gold set and the test host) is ahead. The [changelog](../CHANGELOG.md) has every release.

## What it does

- **Ingestion.** Crawls (one frontier per source, robots.txt, sitemaps, freshness and removal checks, JavaScript pages
  through a headless renderer), URL lists and uploads (virus-scanned by ClamAV) go through parse, clean, chunk,
  personal-data detection, embedding and the index writer. The parser reads PDF, Office and ODF files, images and
  scans (Docling, OCR).
- **Publishing.** A knowledge base is previewed, evaluated against its test questions and then published; a published
  version can be rolled back within 14 days. Nightly evaluation runs alert when recall@10 drops.
- **Retrieval.** Hybrid search: Swedish keyword search and vector search with their ranks fused, and an optional
  reranker. The knowledge API (`POST /v1/retrieve`) offers it without an answer.
- **Chat.** An OpenAI-compatible chat API (`POST /v1/chat/completions`) for Open WebUI and other clients. An assistant
  is a model; its answers cite numbered sources with each document's date and when it was last checked. When the
  sources have no answer, it says so and gives the assistant's contact links. The optional `ume` member adds the
  outcome, related documents, follow-up questions and an escalation hand-off.
- **Administration.** An admin UI for knowledge bases, sources, assistants, apps and keys, costs and settings, with a
  setup wizard for sign-in. Departments own their knowledge bases and assistants.

## How it runs

The pilot runs on one Linux host with Docker Compose: `rag-postgres`, `rag-api` (chat and knowledge), `rag-admin` (the
admin UI and its API), `rag-worker` (ingestion), `rag-parser` and `rag-clamav`, plus the one-shot `rag-migrate`. The
same services run as one Portainer stack together with the gateway, or on Kubernetes.

- Every model call goes through the gateway with an explicit residency ceiling (on-prem or EU), so the gateway refuses
  any route further out.
- Sign-in is OIDC: Keycloak, Entra ID or AD FS.
- Security events go to the municipality's SIEM under one logger category ([security events](security-events.md)).

## Where to go next

- How knowledge bases, assistants, apps and keys and sign-in work together: [how it fits together](how-it-works.md).
- Try it on your machine: [local development](local-development.md).
- Install a release next to the gateway: [quick start with Compose](compose-quick-start.md), or
  [Portainer](portainer.md).
- Operate it: [production setup](production-setup.md), [runbook](runbook.md), [upgrade guide](upgrade-guide.md).
- The design behind it lives in the repository under [`plans/`](../plans/README.md).

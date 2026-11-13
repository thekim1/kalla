# Gateway integration contract

`integrations.json` is the gateway's published integration contract (`docs/openapi/integrations.json` in
github.com/thekim1/llmgateway), copied unchanged: `/v1/ume/context`, `/v1/models` with the `ume` facts, and the parts
of `/v1/chat/completions` and `/integration/v1` an integration uses.

- **Taken from:** thekim1/llmgateway release `v0.10.1` (commit `d828732`). Replace it with each later gateway
  release's copy. It differs from `v0.10.0` (`80015ad`) only in the `x-ume-on-behalf-of` description: an acted key that
  is disabled, expired or revoked answers `403` (`key_disabled`, `key_expired`, `key_revoked`), never `401`, which is
  about the credential key itself.
- **Checked by:** `tests/Ume.Rag.Api.Tests/FakeGatewayContractTests.cs`. The FakeGateway's answers must conform to it:
  a field the document does not describe, or a required one that is missing, fails (`GatewayContract` in TestKit, as
  the gateway's own `IntegrationContract`).

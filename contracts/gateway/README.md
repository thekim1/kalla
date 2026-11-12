# Gateway integration contract

`integrations.json` is the gateway's published integration contract (`docs/openapi/integrations.json` in
github.com/thekim1/llmgateway), copied unchanged: `/v1/ume/context`, `/v1/models` with the `ume` facts, and the parts
of `/v1/chat/completions` and `/integration/v1` an integration uses.

- **Taken from:** thekim1/llmgateway release `v0.10.0` (commit `80015ad`). Replace it with each later gateway
  release's copy. It differs from the earlier pin (#84 at `7b678a1`) only by `ContextDepartment.member_groups` and the
  feature `member-groups`.
- **Checked by:** `tests/Ume.Rag.Api.Tests/FakeGatewayContractTests.cs`. The FakeGateway's answers must conform to it:
  a field the document does not describe, or a required one that is missing, fails (`GatewayContract` in TestKit, as
  the gateway's own `IntegrationContract`).

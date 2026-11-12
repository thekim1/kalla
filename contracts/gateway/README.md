# Gateway integration contract

`integrations.json` is the gateway's published integration contract (`docs/openapi/integrations.json` in
github.com/thekim1/llmgateway), copied unchanged: `/v1/ume/context`, `/v1/models` with the `ume` facts, and the parts
of `/v1/chat/completions` and `/integration/v1` an integration uses.

- **Taken from:** thekim1/llmgateway#84 at `7b678a1` (identical at the PR's head `7b5f4e3`). Replace it with the merge
  commit's copy once #84 is merged, and with each later gateway release's.
- **Checked by:** `tests/Ume.Rag.Api.Tests/FakeGatewayContractTests.cs`. The FakeGateway's answers must conform to it:
  a field the document does not describe, or a required one that is missing, fails (`GatewayContract` in TestKit, as
  the gateway's own `IntegrationContract`).

# Gateway integration contract

`integrations.json` is the gateway's published integration contract (`docs/openapi/integrations.json` in
github.com/thekim1/llmgateway), copied unchanged: `/v1/ume/context`, `/v1/models` with the `ume` facts, and the parts
of `/v1/chat/completions` and `/integration/v1` an integration uses.

- **Taken from:** thekim1/llmgateway release `v0.10.3` (commit `ba22f43`), unchanged since `v0.10.2` (`81d3e43`).
  Replace it with each later gateway release's copy. `v0.10.2` differs from `v0.10.1` (`d828732`) only in the
  context's `gateway.integration_api_url` and `gateway.organisation_name` (features `integration-api-url` and
  `organisation-name`), both required and null when unset.
- **Checked by:** `tests/Ume.Rag.Api.Tests/FakeGatewayContractTests.cs`. The FakeGateway's answers must conform to it:
  a field the document does not describe, or a required one that is missing, fails (`GatewayContract` in TestKit, as
  the gateway's own `IntegrationContract`).

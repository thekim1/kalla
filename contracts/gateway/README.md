# Gateway integration contract

`integrations.json` is the gateway's published integration contract (`docs/openapi/integrations.json` in
github.com/thekim1/fors), copied unchanged: `/v1/fors/context`, `/v1/models` with the `fors` facts, and the parts
of `/v1/chat/completions` and `/integration/v1` an integration uses.

- **Taken from:** thekim1/fors pull request #114 (`b8d3fa1`, the rename to Fors), to be replaced with release `v0.12.0`'s
  copy before this merges. It renames `kalla` to `fors` throughout (the context's `object` `fors.context`, the model facts
  under `fors`, the `x-fors-*` headers) and keeps a deprecated copy of the facts under `ume`. Release `v0.11.0`
  (`d128f57`) differs from release `v0.10.3` (`ba22f43`) only in the context's `gateway.oidc_authority` (feature `oidc-authority`), required and
  null when unset. `v0.10.3` is unchanged since `v0.10.2` (`81d3e43`), which differs from `v0.10.1` (`d828732`) only in
  the context's `gateway.integration_api_url` and `gateway.organisation_name` (features `integration-api-url` and
  `organisation-name`), both required and null when unset.
- **Checked by:** `tests/Kalla.Api.Tests/FakeGatewayContractTests.cs`. The FakeGateway's answers must conform to it:
  a field the document does not describe, or a required one that is missing, fails (`GatewayContract` in TestKit, as
  the gateway's own `IntegrationContract`).

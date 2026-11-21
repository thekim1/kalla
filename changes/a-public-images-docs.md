- **The guides say Källa's images are public:** `ghcr.io/thekim1/kalla/*` pulls without a registry login since 0.9.0,
  so the Portainer, Compose quick start, production, runbook and Kubernetes guides no longer ask for a `ghcr.io` token
  (Ume.Rag's `ghcr.io/thekim1/ume-rag/*` images up to 0.8.0 still need one). The site names the gateway Fors and the
  credential keys `rag-api`, `rag-admin` and `rag-worker`.

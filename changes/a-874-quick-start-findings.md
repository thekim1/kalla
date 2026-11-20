- **rag-bootstrap names the setup wizard's address with its port** (#874): without the TLS proxy serving rag-admin's
  host name, the closing line gives `https://<admin host>:<UME_RAG_ADMIN_PORT, 9543>/setup` (and the form behind a
  reverse proxy of your own), with it the proxy's address. rag-bootstrap now gets `UME_RAG_ADMIN_PORT` and
  `UME_RAG_PROXY_PORT` for it. `init-deployment.sh`'s next steps name the same address.
- **`init-deployment.sh` says to run as root when it cannot enter `--gateway-secrets-dir`** (#874), instead of a bare
  `cd: Permission denied`.
- **The Compose quick start says the cost and budget read-back is off after it** (#874), and points at
  production-setup.md's *Cost and budget read-back*.

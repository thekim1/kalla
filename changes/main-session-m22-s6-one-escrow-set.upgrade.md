- **Escrow the keys again** after the update, with the new `deploy/escrow-keys.sh` (Portainer: `--project <stack>`,
  from the release bundle), then `--verify` it. The new set also holds the pepper, the database roles' passwords, the
  conversation id keys, the Data Protection certificate and the internal CA, in a new layout (`rag-secrets/…`,
  `rag-certs/…`). `--verify` fails on a set made by an earlier release, so keep the old set until the new one prints
  `PASS`. The pepper and certificate copies kept separately until now are in the set, and can go once it is sealed.

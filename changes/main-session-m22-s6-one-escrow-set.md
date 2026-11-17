- **One escrow set** (M-22 S6): `deploy/escrow-keys.sh` writes everything a restored deployment needs that no backup
  holds into one set: the pepper, the blob keys, the pgBackRest passphrase, the database roles' passwords, the
  conversation id keys, the Data Protection certificate and the internal CA. It works the same for every form: a Compose installation (`--env-file`), a Portainer stack
  (`--project <stack>`) and Kubernetes' directories (`--secrets-dir`, `--certs-dir`). `--restore` writes a set into
  new volumes before the first start, and refuses to replace a file that differs. It replaces the guides' separate
  backup of the pepper and the certificate, and the runbook's hand-written restore commands.

- **The env file lives outside the bundle** (#1101 D4): `init-deployment.sh` writes `/etc/kalla/.env`, in the state
  directory `KALLA_STATE_DIR`, and the gateway's CA beside it.
  - Every script reads the state directory's env file, else `deploy/.env`.
  - An upgrade unpacks the new bundle and runs its `init-deployment.sh`, which points `COMPOSE_FILE` at it.
  - A second state directory, or a `deploy/.env` beside the state directory's, is refused.

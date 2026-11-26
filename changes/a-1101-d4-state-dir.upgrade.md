- **`deploy/.env` moves to `/etc/kalla/.env`** (#1101 D4, Compose hosts).
  - Run the new bundle's `sudo deploy/init-deployment.sh` with no options, with the old `deploy/.env` copied into the
    new `deploy/` first. It moves the file to `/etc/kalla/.env`, still mode 600, owned by the user who ran sudo. A
    `--gateway-ca-file` given again goes to `/etc/kalla/gateway-ca/`.
  - Then re-run `sudo deploy/host/install.sh`: the units read the env file's path from `/etc/kalla/host.env`.
  - `dc` is now `docker compose --env-file /etc/kalla/.env`.

- **`deploy/doctor.sh` blocks a deployment without backups** (#1121): no `compose.backup.yaml` is now a `BLOCK` line.
  On a test host you can rebuild, pass `--no-backups-ok` (*Upgrade procedure (Compose host)*, *Your host differs from
  the bundle*).

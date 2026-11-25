- **`deploy/doctor.sh`, a check before every upgrade of a Compose host** (#1101): read-only, it reports the project
  (found by its containers' labels, never a guess between two), the services' versions, each volume's host path and free
  space (named, bind-backed or a direct bind), the overlays and profiles, the pgBackRest stanza, the WAL backlog and the
  newest backups, the newest key escrow set (`--escrow-dir`), and which deploy directory the host units run from. It
  exits 1 on what blocks an upgrade: no running project or two without `--project`, and with backups configured a
  stanza that isn't ok, a WAL backlog beyond 5 minutes, or no backup in 26 hours (`--max-backup-age`).

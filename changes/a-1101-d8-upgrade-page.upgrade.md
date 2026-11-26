- **One upgrade procedure for a Compose host** (#1101): *Upgrade procedure (Compose host)* below replaces *Upgrade
  procedure (single slot)*. It starts with the new release's `deploy/doctor.sh`, run from the unpacked bundle with
  `--env-file` pointing at the live `deploy/.env`, and ends with the doctor again. Step 5 lists what to carry forward
  when `deploy/` is replaced: `deploy/.env`, `deploy/gateway-ca/` and any other directory a `KALLA_*_DIR` names there,
  and your own overlays, best kept outside `deploy/`. The box *Your host differs from the bundle* says what changes
  when the doctor shows another project name, data on a host disk, changed ports, host units from another directory or
  no backups.

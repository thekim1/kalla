- **The host units act on the project by name** (#1101 D6): `deploy/host/install.sh` writes `/etc/kalla/host.env`,
  which names the deploy directory and the Compose project. Every unit reads that file, so a project that isn't called
  `kalla` is backed up, and the units follow a new bundle's directory.
  - `install.sh --check` verifies the units and `host.env`.
  - `deploy/doctor.sh` blocks when the units act on another project.

- **Data on a host disk is a supported layout** (#1101 D1): `init-deployment.sh --data-root DIR` adds
  `compose.host-paths.yaml`, which binds `kalla-postgres-data`, `kalla-blobs` and the two local backup volumes to
  `DIR/postgres`, `DIR/blobs`, `DIR/pgbackrest` and `DIR/upload-copy`. It creates them with their owners, refuses a
  non-empty directory that isn't Källa's, and refuses a deployment whose volumes exist already. Named volumes stay the
  default.

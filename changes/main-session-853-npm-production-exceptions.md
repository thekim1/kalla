- **The admin UI's production npm audit takes dated exceptions, only for a package the bundle doesn't contain**
  (#853): `scripts/audit/npm-production-exceptions.txt` lists an advisory by id and package with a last day, and
  `audit.py npm-production` fails when an excepted package appears in the bundle, as listed by a build with source maps.
  The first is picomatch's ReDoS (GHSA-fc5q-39xc-vjpf, no fixed version), which vue-router declares for its build-time
  plugin. It applies until 2026-12-18.

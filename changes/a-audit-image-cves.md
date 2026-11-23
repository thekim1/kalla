- **Debian security fixes in the postgres, clamav and parser images**: the nightly image scan of 0.9.2 failed on High
  findings with a fix in Debian 13's Kerberos libraries (krb5 1.21.3-5+deb13u2, CVE-2026-112314, -112320 to -112323,
  -119079) and glibc (2.41-12+deb13u5, CVE-2026-19499, -5435). The pinned base images have no rebuild with them yet,
  so `kalla/postgres` and `kalla/clamav` install the fixed packages on top of their base, and `kalla/parser` takes
  them from the Debian snapshot of 2026-11-23. Three stale FFmpeg exceptions in the scan's exception list are gone.

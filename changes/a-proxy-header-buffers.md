- **No more 502 after signing in**: after a release, users coming back from Keycloak to Källa's or the gateway's
  admin could get `502 Bad Gateway` until they cleared their cookies. The sign-in response's cookies outgrew nginx's
  4 KB header buffer. The stack's TLS proxy and `deploy/proxy/nginx.conf.example` now give headers 32 KB.

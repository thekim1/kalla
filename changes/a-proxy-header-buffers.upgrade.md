- **The stack's TLS proxy** (profile `proxy`): kalla-bootstrap writes its new nginx.conf at the update, but the running
  `kalla-proxy` reads it only at start. After the update, restart `kalla-proxy` (Portainer: *Containers*, select,
  *Restart*; Compose: `dc restart kalla-proxy`). Without that, users can still get `502 Bad Gateway` after signing in
  until they clear their cookies.
- **A proxy of your own in front of Källa or the gateway:** add `proxy_buffer_size 32k; proxy_buffers 8 32k;
  proxy_busy_buffers_size 64k; large_client_header_buffers 4 32k;` at its `http` level (Nginx Proxy Manager: each
  proxy host's *Advanced* tab, or `/data/nginx/custom/http_top.conf`), then reload it. An F5 or another proxy: allow
  response headers of at least 32 KB. **Check:** sign in with a browser that still has old cookies for the host; the
  proxy's log has no `upstream sent too big header`.

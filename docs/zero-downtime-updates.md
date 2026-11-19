# Updates without downtime

By default Ume Rag runs one instance of each service, and an update stops `rag-api` and `rag-admin`, migrates the
database and starts the new version: a minute or two in which Open WebUI, MCP clients and the admin UI get errors. If
they must keep working through an update, run two instances of `rag-api` and `rag-admin` behind the reverse proxy and
replace them one at a time. It is opt-in, as in the gateway (whose
[guide](https://github.com/thekim1/llmgateway/blob/main/docs/zero-downtime-updates.md) this follows): nothing changes
for an installation that does not ask for it. The design is [`repo-and-deployment.md`](../plans/platform/repo-and-deployment.md) §7.3.

| You deploy with | Single instance (default) | Without downtime |
|---|---|---|
| Docker Compose (`compose.rag.yaml`) | `dc up -d --wait` ([upgrade guide](upgrade-guide.md)) | `init-deployment.sh --rolling` and `rolling-update.sh` (below) |
| Portainer (`deploy/portainer/compose.portainer.yaml`) | Update the stack | Not supported: Portainer recreates every container at once |
| Kubernetes | – | Built in: see [Kubernetes](kubernetes.md) |

`dc` stands for `docker compose --env-file deploy/.env`, as in the runbook.

## How it works

All shared state lives in Postgres: the key ring that protects admin sign-ins (Data Protection keys), configuration,
job leases, the background syncs' advisory locks and the retention steps' locks. Any instance can therefore serve any
request, and a user signed in to the admin UI stays signed in when the next request reaches the other instance. Two
things are per instance: the request rate limiters (each slot counts its own, so a client may briefly get up to twice
its limit) and the gateway model sync's run history on the admin's Settings page.

When an instance is told to stop, it:

1. **Stops being ready.** `/health/ready` answers 503 at once, so a load balancer that polls it stops sending new
   requests. `/health/live` stays 200.
2. **Keeps answering for the drain time** (`Shutdown__DrainSeconds`), each response with `Connection: close`, so the
   proxy closes its pooled connection after the current request instead of sending the next one on a connection that
   is about to close (a POST, such as every chat completion, would not be retried and the client would get a 502).
3. **Closes its listener.** The proxy sends new requests to the other instance; the few connections the operating system
   had accepted but the instance never read are reset, and the proxy retries them (nginx settings below).
4. **Finishes the requests it has** within the shutdown timeout (`DOTNET_SHUTDOWNTIMEOUTSECONDS`), including long
   streamed answers, then exits.

The database is migrated before the first new instance starts, so until the last old instance stops the old version
runs against the new schema. That works only when the release's migration is backward compatible.
**Until the first production deployment, a release may not be** (Decisions, "No backward compatibility before
production"): the [upgrade guide](upgrade-guide.md) marks every release **Rolling update: yes** or **no**, and for a
"no" release you stop everything and update as for a single instance (`dc up -d --wait`).

The worker, parser, ClamAV and the renderer keep one instance. The worker is stopped before its replacement starts:
two would exceed its database role's connection limit, and its job leases make the pause harmless (jobs wait, nothing
is lost). Ingestion pauses for that minute; questions and the admin UI do not.

## Docker Compose

The overlay `deploy/compose.rolling.yaml` adds `rag-api-b` and `rag-admin-b` with the same image and settings, on the
next host ports up (8544 and 9544 by default). A reverse proxy of your own lists both ports of each service: something has to
choose the instance, so clients that reach rag-api without the proxy cannot use this. The stack's own TLS proxy (the
profile `proxy`) knows one slot of each, so it cannot be used for this. The database roles' connection
limits already have room for both slots (`ume_rag_api` 26 for 2 × 12, `ume_rag_admin` 12 for 2 × 5). The two slots add
1.5 GB to the memory limits: on the 16 GB host profile check the headroom (production setup, *Sizing*).

### Set it up

New installation:

```bash
./deploy/init-deployment.sh ... --rolling --proxy-address <your proxy's IPv4 address>   # COMPOSE_FILE gets compose.rolling.yaml
sudo deploy/host/install.sh          # with --hardened: the firewall then also admits the proxy to 8544 and 9544
dc up -d --wait
```

`--proxy-address` matters only with `--hardened`, where the host firewall admits only that address to the published
ports; without it the ports are open to whoever reaches the host (they bind `0.0.0.0` unless `UME_RAG_API_BIND` and
`UME_RAG_ADMIN_BIND` say otherwise).

Existing installation: re-run `init-deployment.sh` with only `--rolling` (it keeps every other line of `deploy/.env`;
the certificates and secrets are in the volumes), restart the firewall where it is installed
(`sudo systemctl restart rag-firewall`), `dc up -d --wait`, and add the second slots to the proxy.

**nginx** (`deploy/proxy/nginx.conf.example`: fill in its placeholders and enable the lines marked `#rolling`):

```nginx
upstream ume_rag_api {
    server docker-host:8543;
    server docker-host:8544;
    keepalive 32;
}
upstream ume_rag_admin {
    server docker-host:9543;
    server docker-host:9544;
}
```

and in the rag-api and admin `location`s:

```nginx
proxy_next_upstream error timeout non_idempotent;
proxy_next_upstream_timeout 10s;
proxy_next_upstream_tries 2;
proxy_connect_timeout 5s;
```

`proxy_next_upstream` sends a failed connection to the other slot, `non_idempotent` lets nginx retry a POST too. A retry
happens only when the connection failed before any response, and only within 10 seconds, so a request that ran long
and then failed is never repeated. The upload location (`PUT /api/upload-batches/...`) is not retried: its body is not
buffered, so a failed upload is retried by the admin UI.

**Nginx Proxy Manager:** a proxy host has one upstream. Put the `upstream` blocks in a custom configuration
(`/data/nginx/custom/http_top.conf`) and point the proxy host's *Advanced* tab at them (`proxy_pass https://ume_rag_api;`).

**F5, HAProxy or another load balancer with health monitors:** add both ports to the pool and monitor
`GET /health/ready` over HTTPS (expect 200). Let it resend a request when the connection fails before a response (F5:
*Reselect Tries*; HAProxy: `retry-on conn-failure empty-response` with `option redispatch`). Set the drain time above
the monitor interval times the failures before a member is marked down, for example `UME_RAG_DRAIN_SECONDS=15` with a
5-second interval and 2 failures.

### Update

Read the [upgrade guide](upgrade-guide.md) for the release, take the backup of its step 3, set the image pins in
`deploy/.env` (or unpack the new release bundle over `deploy/`), then:

```bash
./deploy/rolling-update.sh            # --pull to pull the images first
```

It checks that all four slots are healthy, runs `rag-bootstrap` and `rag-migrate`, then replaces `rag-api`, `rag-api-b`, `rag-admin` and
`rag-admin-b` one after the other. It waits until each new slot is healthy, plus `UME_RAG_ROLLING_SETTLE_SECONDS` (15)
so the proxy takes it back into rotation, before stopping the next, and refuses to stop a slot while its partner is
unhealthy. Then it updates the worker (stop, then start), the parser, ClamAV and the renderer (the last two where their
profiles are on), each only if it changed.
A slot whose image did not change is left alone; `--force` recreates it anyway, for example after `rag-bootstrap` has
re-issued its certificate.

A new `RAG_POSTGRES_IMAGE` restarts the database, which is never rolling: the script stops with an error unless you pass
`--with-postgres`, which replaces `rag-postgres` (a short outage of every service; announce it), runs
`ALTER EXTENSION vector UPDATE` and then continues as above. Other superuser steps the release lists still run by hand
first (upgrade guide, *Superuser steps*).

### Settings

In `deploy/.env`, read by `compose.rolling.yaml`:

| Variable | Default | Meaning |
|---|---|---|
| `UME_RAG_API_B_PORT`, `UME_RAG_ADMIN_B_PORT` | 8544, 9544 | Host ports of the second slots (bound to `UME_RAG_API_BIND` / `UME_RAG_ADMIN_BIND`) |
| `UME_RAG_DRAIN_SECONDS` | 5 | Seconds a stopping slot keeps answering with `Connection: close` (`Shutdown__DrainSeconds`) |
| `UME_RAG_SHUTDOWN_TIMEOUT_SECONDS` | 300 | Longest time a stopping rag-api finishes its requests, drain included (rag-admin: 60) |
| `UME_RAG_ROLLING_SETTLE_SECONDS` | 15 | Pause after each slot is healthy (environment variable for `rolling-update.sh`) |

`Shutdown__DrainSeconds` (0–300, default 0) is a setting of every service, so it also works outside these files.

## What is still interrupted

- **Ingestion** pauses while the worker restarts (about a minute); queued and running jobs resume.
- **Requests longer than the shutdown timeout** (300 s for rag-api) are cut off. Raise `UME_RAG_SHUTDOWN_TIMEOUT_SECONDS`
  (and `stop_grace_period` in `compose.rolling.yaml`) if you have longer ones.
- **Postgres** is a single instance: a new image is a short outage (`--with-postgres`).
- **Releases marked "Rolling update: no"** (0.5.0, 0.6.0 and 0.8.0 so far) can't be rolled: with two slots, stop the
  services the release's entry in the [upgrade guide](upgrade-guide.md) names before its migration. (On one slot
  `rag-migrate` does that itself from 0.8.0.)
- **The single-instance installation's first rolling update** stops instances that had no drain configured; a few POSTs
  can fail at that moment, later updates do not have this.

## Tested

`deploy/tests/compose-rolling-e2e.sh` builds nothing: it runs against images you have (`compose.rag.yaml`'s pinned
images, or others through `RAG_API_IMAGE`, `RAG_ADMIN_IMAGE`, `RAG_MIGRATIONS_IMAGE` and `RAG_POSTGRES_IMAGE`, such as
`ume-rag/*:latest` built locally). It starts `compose.rag.yaml` + `compose.rolling.yaml` with the `deploy/.env` that
`init-deployment.sh --rolling` writes, puts nginx with `deploy/proxy/nginx.conf.example` (both slots of each service)
in front, replaces every slot with `rolling-update.sh --force` while clients send POST and GET requests to rag-api and
load the admin UI, and fails on a single lost request. The proxy runs on the stack's `rag-edge` network and reaches the
slots by name: Docker Desktop's port forwarder accepts connections to a restarting container's published port and then
resets them, which a Linux host does not do.

| Run (2026-11-10, Docker Desktop on WSL 2, images built from `main`) | Requests during the update | Lost |
|---|---|---|
| nginx in front, all four slots replaced | 98 127 POST, 289 179 GET, 209 721 admin UI | 0 |

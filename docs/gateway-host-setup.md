# Setting up Ume.Rag for phase 1 next to the LLM gateway

A step-by-step guide for whoever sets up the RAG platform on the machine that already runs the team's LLM gateway:
a person, or an infrastructure agent working through a shell. It covers milestone 1 of phase 1: the platform reads a
handful of public umea.se pages, embeds them through the gateway with the EU-hosted `text-embedding-3-large` model,
and stores them as searchable chunks. Answers in Open WebUI (milestone 2) need code that is not merged yet; §12 says
what changes then.

Background: [`docs/production-setup.md`](production-setup.md) (the full production install),
[`docs/test-host.md`](test-host.md) (the pilot test host), [`docs/p1a-local-run.md`](p1a-local-run.md) §4 (the same
milestone on a developer machine), and the Decisions table in [`01-general-idea.md`](../01-general-idea.md) §10, rows
"Interim gateway for P1a" and "Pilot embedding model, U-b".

## 0. Rules for whoever runs this

- **Secrets never go into the repository, a log you share, a chat or a ticket.** The gateway's address, its keys, the
  Keycloak client secrets and everything under `deploy/secrets` and `deploy/certs` stay on the host. This guide uses
  placeholders: `<gateway url>` (the gateway's public HTTPS address, for example `https://llmgateway.example.se`),
  `<keycloak url>`, `<rag host>`. When a command needs a key, it reads it from a file; don't paste keys on a command
  line, because the process list and the shell history keep them.
- **Steps marked HUMAN need a person's yes before you run them.** They restart Docker (which stops the gateway's
  containers for a moment), change the host firewall, or need something only a person can provide. An agent stops at
  each one, says what it is about to do and why, and waits.
- **Stop when a check fails.** Every step ends with a check. If the result differs from the expected one, stop, collect
  the output named in §13, and report. Don't improvise around a failed check, and don't retry the same command more
  than twice.
- **The gateway comes first.** Nothing in this guide may stop, reconfigure or slow down the gateway beyond the HUMAN
  steps. If a step would touch the gateway's Compose project (`ume-gateway`), its networks, its volumes or its proxy
  configuration, stop and ask.
- **Be polite to umea.se.** Seed a handful of pages (five is plenty). The worker identifies itself as
  `UmeaKommunBot/1.0`, reads robots.txt and keeps a per-host budget (all changeable per host by a platform admin, see
  the runbook's "Crawl politeness per host"; leave them at the defaults here), but a large crawl of umea.se needs the
  web editors' go-ahead (register item U1) first.
- **Commands run from the checkout's root** (the directory with `deploy/` and `src/`), as a user in the `docker` group
  unless the step says `sudo`.

## 1. What you end up with

| Part | State after this guide |
|---|---|
| Postgres (pgvector, Swedish search), rag-migrate, rag-api, rag-admin | running in Compose project `ume-rag`, listeners on `127.0.0.1:8543` (api) and `127.0.0.1:9543` (admin) |
| rag-worker, rag-parser, rag-clamav | running below `rag-ingestion.slice` (CPU and memory capped) |
| Host firewall `UME-RAG-*` | egress per RAG network; RAG containers reach only the gateway, Keycloak, DNS, the ClamAV mirror and public web sites |
| Backups | pgBackRest to the agreed target, a first full backup |
| Embedding profile `pilot-embedding` | `text-embedding-3-large`, 1024 dimensions, ceiling `Eu` (once the gates in §2 are open) |
| A URL-list source with a few umea.se pages | synced into live chunks with 1024-dimension vectors |

Not part of this guide: Open WebUI and answers (milestone 2, §12), signing in to the admin UI (needs RAG's Keycloak
clients, stream B's realm additions), a public URL for rag-api (only needed for Open WebUI).

## 2. Gates: what must be merged first

The platform is built "code first", so a few pieces this guide relies on are still open pull requests. Check them
before you start; with `gh` installed:

```bash
for n in 123 127; do gh pr view "$n" --repo thekim1/ume-rag-platform --json number,state,title --jq '"#\(.number) \(.state) \(.title)"'; done
gh issue view 142 --repo thekim1/ume-rag-platform --json state,title --jq '"#142 \(.state) \(.title)"'
```

| Gate | Needed for | Without it |
|---|---|---|
| #123 merged (gateway client, `EmbeddingProfile:*` seeder, canary) | the `pilot-embedding` profile is seeded at start | §9 refuses: "no active embedding profile pilot-embedding" |
| #142 closed (the profile's `RequestDimensions`, `dimensions` sent on every embedding call) | the model returns 1024 values, not its native 3072 | embedding fails the profile's dimension check; nothing goes live |
| #119 (real-gateway mode, organisation sync): merged | the admin's model catalogue sync and the canary record | nothing to wait for |
| #127 merged (knowledge service) | a retrieval query in §10 | check the chunks with SQL only |

You can do §3 to §8 before the gates open (the stack runs, nothing is embedded). Do §9 only when #123 and #142 are
done. Pull the newest `main` and rebuild the images (§5) after they merge.

## 3. Inputs you need

Collect these before you start. Files go in a directory outside the checkout that only you can read
(`install -d -m 700 ~/rag-inputs`).

| Input | Where it comes from | How it is used |
|---|---|---|
| `<gateway url>` | the gateway's public HTTPS address (its admin UI is a different host) | `--gateway-url` |
| `<data api url>` | the gateway's Data API (`/v1/catalog/*`), a service of its own: its public HTTPS address, or `https://<gateway host>:10443` by default. Not the gateway URL | `--org-sync-url` |
| Three gateway keys, one each for `api`, `admin`, `worker` | gateway admin UI: one key per RAG service, **allowed residency `Eu` only** | files `~/rag-inputs/api-eu.key`, `admin-eu.key`, `worker-eu.key` (the key alone, one line) |
| A gateway Data API key with `catalog.read` | gateway admin UI (Data API keys) | `~/rag-inputs/org-sync.key`; the admin syncs departments and the model catalogue with it |
| `<keycloak url>/realms/ume` | the Keycloak on this host (realm `ume`, shared with the gateway) | `--oidc-authority`. If realm `ume` doesn't exist yet, use `https://keycloak.invalid/realms/ume`: the stack runs, only admin sign-in fails |
| `<rag host>` | the host's DNS name | `--api-host`, `--admin-host` |
| Backup target | agreed with IT: a mounted share (`/mnt/backup/ume-rag`), an S3 bucket, or `local` (a Docker volume on the same disk: test hosts only) | `--backup-target` |
| Escrow media | a USB stick or a sealed share for the long-lived keys | §8 |
| The embedding model name | `text-embedding-3-large` (its provider must be marked `Eu` in the gateway) | `deploy/.env`, §7 |

Two settings on the gateway side (a person with gateway admin rights):

- **The embedding deployment's `ContextWindow` may be set** (8191 for `text-embedding-3-large`). From v0.9.0 the
  gateway checks each embedding input against it, not the whole batch.
- **Each RAG key allows residency `Eu` only.** The platform also sends a ceiling with every call, but the key is the
  second guard.

## 4. Preflight checks (read-only)

Run each block and compare with "expected". Nothing here changes the host.

**4.1 Operating system and Docker**

```bash
uname -a; cat /etc/os-release | head -3
docker version --format '{{.Server.Version}}'          # expected: 28 or later
docker compose version --short                          # expected: 2.24 or later
docker info --format '{{.CgroupDriver}} {{.CgroupVersion}}'   # expected: systemd 2
systemctl is-system-running                             # expected: running (or degraded with a known cause)
nproc; free -g | awk '/Mem:/ {print $2 " GB total, " $7 " GB available"}'
df -h /var/lib/docker
```

Expected: Linux with systemd, Docker 28+, Compose 2.24+, cgroup driver `systemd`, cgroup version `2`.

Sizing next to the gateway: the RAG stack's 16 GB profile caps all its containers together at about 11 GB of memory
and the ingestion zone at about 3 CPUs (repo-and-deployment §6.2). Add the gateway's own use. If `available` memory is
below 12 GB or the host has fewer than 6 cores, stop and report: the two stacks would compete. 20 GB of free disk is
enough for milestone 1.

**4.2 Docker's address pools and the `172.30.0.0/16` range**

RAG's Compose networks use fixed subnets in `172.30.0.0/16` (`deploy/host/docker-user-firewall.sh --print-subnets`), so
the host firewall can give each its own rule. Docker must not have given that range to another network, the gateway's
included (the gateway's networks have no fixed subnets; Docker picks them from its default pools, which include
`172.30.0.0/16`).

```bash
docker network ls -q | xargs docker network inspect --format '{{.Name}} {{range .IPAM.Config}}{{.Subnet}} {{end}}' | sort
cat /etc/docker/daemon.json 2>/dev/null || echo "no daemon.json"
```

Expected: no subnet starting with `172.30.`, and ideally a `default-address-pools` entry outside `172.30.0.0/16`.
If a network (the gateway's or another) already uses `172.30.x.x`: **stop (HUMAN)**. Moving it means recreating that
network, which restarts the containers on it. Options for the person: recreate that network after setting
`default-address-pools` (§6), or move RAG's subnets with the firewall script's `--subnet` option and matching Compose
overrides (more work; ask the RAG team).

**4.3 Ports**

```bash
ss -ltnp | grep -E ':(8543|9543)\b' || echo "8543 and 9543 free"
```

Expected: both free. The gateway uses 8443 (gateway), 9443 (admin) and 10443 (Data API) by default; RAG uses 8543 and
9543. If either is taken, pick others with `--api-port` and `--admin-port` in §7.

**4.4 The gateway answers with your worker key**

This proves the key, the model and the residency before anything else is installed. The key goes to curl through a
header file, never on its command line:

```bash
umask 077; tmp=$(mktemp -d)
printf 'Authorization: Bearer %s\n' "$(tr -d '\r\n' < ~/rag-inputs/worker-eu.key)" > "$tmp/h"
curl -sS -D "$tmp/headers" -o "$tmp/body" -w 'HTTP %{http_code}\n' \
  -H @"$tmp/h" -H 'x-ume-max-residency: Eu' -H 'Content-Type: application/json' \
  -d '{"model":"text-embedding-3-large","input":["Hej från Umeå kommun"],"dimensions":1024,"encoding_format":"float"}' \
  '<gateway url>/v1/embeddings'
jq -r '"dims=\(.data[0].embedding|length)"' "$tmp/body"; grep -i '^x-ume-residency' "$tmp/headers"
rm -rf "$tmp"
```

Expected: `HTTP 200`, `dims=1024`, `x-ume-residency: Eu`. Repeat with `api-eu.key` and `admin-eu.key`.

| Result | Meaning | What to do |
|---|---|---|
| 401 | the key is wrong, disabled or expired | ask the gateway admin |
| 403 `max_residency_not_met` | the model's provider is not marked `Eu`, or the key doesn't allow `Eu` | ask the gateway admin; the pilot may only use EU-hosted models |
| 400 `context_length_exceeded` | the gateway is older than v0.9.0 and checks the whole batch against the deployment's `ContextWindow` | ask for the gateway to be upgraded, or the `ContextWindow` cleared until then (§3) |
| `dims=3072` | the gateway didn't pass `dimensions` | report; the gateway should forward the body unchanged |
| a TLS error | the certificate isn't publicly trusted | §7 needs `--ca-file` with the gateway's CA chain |

**4.5 How the gateway's address resolves on this host**

```bash
getent ahostsv4 "$(echo '<gateway url>' | sed -E 's#^https?://##; s#[:/].*##')" | awk '{print $1}' | sort -u
ip -4 -o addr show | awk '{print $4}'
```

If the gateway's address is one of this host's own addresses, or it is served by a proxy container on this host,
RAG's containers reach the gateway "on the host itself". The host firewall (from the commit that added this guide on)
lets the allow-listed endpoints through in that case; §8 checks it. Note the result for the report.

## 5. Get the code and build the images

```bash
git clone https://github.com/thekim1/ume-rag-platform.git ~/ume-rag && cd ~/ume-rag
git log -1 --format='%h %s'          # note the commit for the report
```

There is no release with the phase 1 code yet (`v0.1.0` is phase 0), so build the images on the host from this
commit. It needs the .NET 10 SDK (as `global.json` says) and Node 24 for the admin UI; if the host shouldn't have
them, build on another machine and push to a registry (`docs/production-setup.md`, "Files and images").

```bash
docker build -f images/postgres/Dockerfile -t ume-rag/postgres:latest .
docker build -t ume-rag/clamav:latest images/clamav
docker build -t ume-rag/parser:latest src/parser
(cd src/admin-ui && npm ci && npm run build)
for p in Api Worker MigrationService; do dotnet publish src/Ume.Rag.$p -c Release -t:PublishContainer; done
dotnet publish src/Ume.Rag.AdminApi -c Release -t:PublishContainer -p:PublishAdminUi=true
docker image ls --format '{{.Repository}}:{{.Tag}} {{.CreatedSince}}' | grep '^ume-rag/'
```

Expected: seven images `ume-rag/{postgres,clamav,parser,api,worker,migrations,admin}:latest`, all just created. The
parser image is the largest (Docling, OCR models) and takes the longest. Building uses every core: run it outside the
gateway's busy hours, or prefix the commands with `nice -n 10`.

## 6. Docker daemon settings (HUMAN)

Needed only if §4.1 showed a cgroup driver other than `systemd`, or §4.2 showed no `default-address-pools` outside
`172.30.0.0/16`. **Restarting Docker stops every container on the host, the gateway's included, for the length of the
restart** (the containers come back through their restart policies). Get a person's yes and a time.

`/etc/docker/daemon.json` (merge with what is there; keep existing keys):

```json
{
  "exec-opts": ["native.cgroupdriver=systemd"],
  "default-address-pools": [{ "base": "10.210.0.0/16", "size": 24 }]
}
```

The pool is an example: a range IT confirms is unused on this network, never one inside `172.30.0.0/16`. Then:

```bash
sudo systemctl restart docker
docker info --format '{{.CgroupDriver}} {{.CgroupVersion}}'     # expected: systemd 2
docker ps --format '{{.Names}} {{.Status}}' | grep -i gateway   # expected: the gateway's containers Up again
```

Check the gateway answers again (§4.4) before you go on.

## 7. Generate the deployment

`init-deployment.sh` writes the internal CA and certificates, every password and key, and `deploy/.env`. It never
overwrites an existing secret, so a re-run is safe. Run it as root so the files get the containers' owners.

```bash
sudo ./deploy/init-deployment.sh \
  --oidc-authority '<keycloak url>/realms/ume' \
  --api-host '<rag host>' --admin-host '<rag host>' \
  --gateway-url '<gateway url>' \
  --gateway-key-file api:Eu="$HOME/rag-inputs/api-eu.key" \
  --gateway-key-file admin:Eu="$HOME/rag-inputs/admin-eu.key" \
  --gateway-key-file worker:Eu="$HOME/rag-inputs/worker-eu.key" \
  --org-sync-url '<data api url>' \
  --org-sync-key-file "$HOME/rag-inputs/org-sync.key" \
  --backup-target '<backup target>' \
  --host-memory 16 --allow-file-keys --yes
```

Notes:

- Listeners stay on `127.0.0.1` (the default without `--proxy`). Nothing in milestone 1 needs them reachable from
  outside; check them with `curl` on the host. For milestone 2 the api goes behind the host's reverse proxy (`--proxy`,
  §12).
- Add `--ca-file <file>` only if §4.4 showed a TLS error.
- Add `--freshclam-proxy <host:port>` if the host reaches the Internet only through a proxy (ClamAV downloads its
  signatures from `database.clamav.net`).
- `--host-memory 24` only on a host where RAG may use 24 GB next to the gateway.
- The script prints the next steps and the Keycloak redirect URIs. Save its output (it contains no secrets) for the
  report.

Then the embedding profile, which the script doesn't write yet. Append to `deploy/.env` (and again after any re-run
of `init-deployment.sh`, which rewrites the file and keeps the old one as `deploy/.env.bak`):

```bash
sudo tee -a deploy/.env >/dev/null <<'EOF'
# Pilot embedding profile (Decisions "Pilot embedding model, U-b"); read by rag-migrate.
UME_RAG_EMBEDDING_MODEL=text-embedding-3-large
UME_RAG_EMBEDDING_DIMENSIONS=1024
UME_RAG_EMBEDDING_REQUEST_DIMENSIONS=true
UME_RAG_EMBEDDING_MAX_INPUT_TOKENS=8191
UME_RAG_EMBEDDING_MAX_RESIDENCY=Eu
EOF
docker compose --env-file deploy/.env config --quiet && echo "compose config ok"
```

Expected: `compose config ok`. The migrator seeds the profile once (insert-if-absent): a later change of these lines
does not change an existing profile; a different model or dimension count needs a new profile (phase 3).

Check the secrets are not readable by others and the inputs can go:

```bash
sudo find deploy/secrets deploy/certs -perm -o+r -type f | head      # expected: nothing except ca.pem files
shred -u ~/rag-inputs/*.key && rmdir ~/rag-inputs                    # the keys now live in deploy/secrets
```

## 8. Host units, firewall, start

**8.1 Host units and firewall (HUMAN: changes the host firewall)**

```bash
deploy/host/docker-user-firewall.sh --dry-run | grep -E 'UME-RAG-(EGRESS|INPUT) -s 172\.30\.3\.0/24' | head
```

Read the dry run first. Expected: `RETURN` rules from the `llm` network (`172.30.3.0/24`) to the gateway's addresses on
port 443 (or the gateway URL's port), in `UME-RAG-EGRESS` (plain and `--ctorigdst`) **and** in `UME-RAG-INPUT`. The
`INPUT` rules are what lets RAG reach a gateway served by this same host. The rules only add `RETURN`s for RAG's own
subnets and rejects for them; they never match the gateway's containers. With the person's yes:

```bash
sudo deploy/host/install.sh            # reads UME_RAG_HOST_MEMORY from deploy/.env
systemctl list-timers 'rag-*'
sudo iptables -S UME-RAG-INPUT | head
```

Expected: `rag-ingestion.slice`, `rag-firewall.service` and its timer, and the backup, backup-status, upload-copy and
restart-unhealthy timers; `UME-RAG-INPUT` holds the gateway `RETURN` rules before the rejects. The firewall runs every
15 minutes and re-resolves host names.

**8.2 Start**

```bash
docker compose --env-file deploy/.env up -d --wait
docker compose --env-file deploy/.env ps --format '{{.Service}} {{.State}} {{.Health}}'
docker compose --env-file deploy/.env exec -T postgres pgbackrest --stanza=ragdb stanza-create
sudo systemctl start rag-backup.service
```

Expected: every service `running healthy`, `rag-migrate` and `rag-blobs-init` `exited` (one-shots). The first start
of `rag-clamav` downloads its signatures (up to 10 minutes) before it reports healthy. If `up --wait` times out, §13.

```bash
sudo curl -sS --cacert deploy/certs/ca.pem https://localhost:8543/health/ready; echo
sudo curl -sS --cacert deploy/certs/ca.pem https://localhost:9543/health/ready; echo
docker compose --env-file deploy/.env logs rag-migrate | grep -i -E 'embedding profile|error' | tail -5
```

Expected: both `Healthy`. Once gate #123 is open, the migrator's log mentions the `pilot-embedding` profile.

**8.3 RAG's containers reach the gateway**

A throwaway container on RAG's `llm` network gets the same firewall rules as rag-api and rag-admin:

```bash
docker run --rm --network ume-rag_llm curlimages/curl:8.11.1 -sS -o /dev/null -w 'HTTP %{http_code}\n' '<gateway url>/v1/models'
```

Expected: `HTTP 401` (no key sent: the gateway answered, which is all this checks). A timeout or "connection refused"
means the firewall blocks the gateway: §13.

**8.4 Escrow the keys (HUMAN: needs the escrow media)**

```bash
sudo deploy/escrow-keys.sh /media/<escrow>          # then: sudo deploy/escrow-keys.sh --verify /media/<escrow>
```

Also keep `deploy/secrets/.pepper` and `deploy/certs/data-protection.pfx` apart from the database backups, and move
`deploy/certs/ca.key` off the host (`docs/production-setup.md`, step 5).

## 9. Seed a URL-list source (after gates #123 and #142)

The admin's set-up endpoints don't exist yet, so a small script writes the department, collection and source directly,
the same rows the admin will write later. On this stack the superuser may only connect over the container's Unix
socket (`deploy/postgres/pg_hba.conf`: `local all postgres peer`; TCP is refused), so the script runs psql inside the
Postgres container as its `postgres` user. No password is involved:

```bash
python3 -I scripts/dev/seed_url_list.py --docker ume-rag-postgres-1 --socket --profile pilot-embedding \
  'https://www.umea.se/<page 1>' 'https://www.umea.se/<page 2>' 'https://www.umea.se/<page 3>'
```

The script needs Python 3.11+ on the host (standard library only).

Pick three to five public pages, for example a price list, an opening-hours page and a contact page. Expected output:
lines for the department (`Pilot (dev seed)`), the collection (`pilot-public`, public and published), the profile
(`pilot-embedding`), the pipeline (`url-list`), the source (`created, N URLs`) and a `sync_job` id (the run id). Write
down the source id and the sync job id.

| Message | Meaning |
|---|---|
| `no active embedding profile pilot-embedding` | gate #123 isn't merged, the image predates it, or §7's `.env` lines are missing: check `docker compose logs rag-migrate` |
| `no published platform pipeline url-list` | the migrator hasn't finished: `docker compose ps rag-migrate` |
| `pg_hba.conf rejects connection … user "postgres"` | `--socket` is missing |

## 10. Check the result

Open a psql session in the container:

```bash
docker compose --env-file deploy/.env exec -u postgres postgres psql -U postgres -d ragdb
```

| Check | SQL | Expected |
|---|---|---|
| The run finished | `SELECT "State", "Added", "Updated", "Unchanged", "Failed" FROM content."Runs" WHERE "Id" = '<sync job id>';` | `succeeded`, `Added` = the number of URLs (a few minutes; first parses are slower) |
| Live chunks with 1024-dimension vectors | `SELECT count(*), min(vector_dims(c."Embedding")) FROM content."Chunks" c JOIN content."Documents" d ON d."LiveChunkSetId" = c."ChunkSetId" WHERE d."SourceId" = '<source id>' AND c."DeletedAt" IS NULL;` | at least one chunk per page, `1024` |
| Per-page outcome | `SELECT "Identity", "Outcome", "Reason" FROM content."RunItems" WHERE "RunId" = '<sync job id>' ORDER BY "Id";` | `added` for each page |
| The canary | `SELECT "Name", "GatewayModel", "Dimensions", "CanaryEmbedding" IS NOT NULL AS recorded, "CanarySimilarity" FROM config."EmbeddingProfiles";` | `pilot-embedding`, `text-embedding-3-large`, `1024`, `t` (the admin records it after its first catalogue sync); the similarity is empty until a platform admin runs **Check embedding canary** (admin UI, Platform → Gateway models), then at least `0.999` |

On the gateway side, its admin UI shows the embedding calls under the worker's key with their cost (a few hundredths of
a cent for a handful of pages).

A retrieval query (after #127): `/v1/retrieve` on `https://localhost:8543` with an api client key. Client keys are
created through the admin API, which needs sign-in (stream B's realm additions); until then the SQL above is the check.

The worker's log, when something looks wrong:

```bash
docker compose --env-file deploy/.env logs --since 30m rag-worker | grep -v -i 'health' | tail -50
```

## 11. Stop, restart, remove

| Goal | Command |
|---|---|
| Stop RAG, keep all data | `docker compose --env-file deploy/.env down` |
| Start again | `docker compose --env-file deploy/.env up -d --wait` |
| Update to a newer commit | `git pull`, rebuild the images (§5), re-run `sudo ./deploy/init-deployment.sh … --yes` with the same options (keeps secrets), re-append §7's lines if they're gone, `sudo deploy/host/install.sh`, then `up -d --wait` |
| Remove RAG completely (HUMAN: deletes its data) | `docker compose --env-file deploy/.env down -v`, `sudo deploy/host/docker-user-firewall.sh --remove`, `sudo systemctl disable --now rag-firewall.timer rag-firewall.service rag-backup.timer rag-backup-status.timer` (and the other `rag-*` units `systemctl list-units 'rag-*'` shows), then delete the checkout. Ask the gateway admin to revoke the three RAG keys and the Data API key |

None of these touch the gateway's Compose project, volumes or networks.

## 12. Milestone 2: what changes for answers in Open WebUI

When stream B's chat API, admin set-up endpoints and Keycloak realm additions are merged:

1. Import RAG's realm additions (`deploy/keycloak/`, from stream B) into realm `ume` on this host's Keycloak, and give
   the clients `ume-rag-admin` and `open-webui` the redirect URIs `init-deployment.sh` prints.
2. Re-run `init-deployment.sh` with `--proxy`, `--upstream-host`, `--oidc-client-secret-file`, and the Open WebUI
   options (`--openwebui-host chat.<domain>`, its OIDC client secret, the api key and client id created in the admin).
   Route the host names through the reverse proxy that already serves the gateway (the script prints the routes).
3. Start Open WebUI (`docker compose -f deploy/compose.openwebui.yaml --env-file deploy/.env up -d --wait`) and mint
   the feedback key (`sudo deploy/openwebui-feedback-key.sh`): [`docs/test-host.md`](test-host.md), "Open WebUI".
4. A chat model on the gateway with an `Eu` provider (every chat model on the interim gateway is EU-hosted).

The run book for that step comes with the chat API.

## 13. Troubleshooting

| Symptom | Likely cause | Check, then fix |
|---|---|---|
| `up` fails: "Pool overlaps with other one on this address space" | a network already uses `172.30.x.x` | §4.2; HUMAN |
| `up --wait` times out on `rag-worker`, `rag-parser` or `rag-clamav` | the slice isn't installed, or cgroup driver isn't `systemd` | `systemctl status rag-ingestion.slice`, `docker info`; §6 and §8.1 |
| `rag-clamav` unhealthy for > 15 min | no signature download (proxy, firewall) | `docker compose logs rag-clamav`; `--freshclam-proxy` in §7 |
| §8.3 times out | the firewall blocks the gateway (an old firewall script without the `INPUT` exception, or the gateway's name resolves to an address not in the rules) | `sudo iptables -S UME-RAG-INPUT`, `sudo iptables -S UME-RAG-EGRESS \| grep 172.30.3.0`; re-run `sudo systemctl start rag-firewall.service`; compare with §4.5 |
| Runs fail with `embed.input_rejected` | the gateway refused a batch (`context_length_exceeded`) | a gateway older than v0.9.0 checks the whole batch: §3, upgrade it or clear the deployment's `ContextWindow`; on v0.9.0 or later, report it (a chunk is far below the window) |
| Runs fail at the embed step on dimensions, or **Check embedding canary** reports the wrong number of dimensions | 3072 values came back: `RequestDimensions` isn't set or gate #142 isn't in the image | §7's `.env` lines, rebuild after #142 |
| 403 from the gateway in the worker log | the key doesn't allow `Eu`, or the model's provider isn't `Eu` | §4.4 |
| Pages end `skipped` with `robots.*` | robots.txt disallows the path | pick other pages |
| Everything else | | collect the output below and report |

Collect for a report (none of it contains secrets; check before sharing):

```bash
git log -1 --format='%h %s'
docker compose --env-file deploy/.env ps
docker compose --env-file deploy/.env logs --since 1h rag-migrate rag-worker rag-admin | tail -200
systemctl list-units 'rag-*' --no-pager
sudo iptables -S UME-RAG-EGRESS | head -40; sudo iptables -S UME-RAG-INPUT
```

## 14. Report template

When you are done (or stopped), report in this shape:

```
Ume.Rag phase 1 setup on <host>, <date>
Commit: <hash>
Gates: #123 <state>, #127 <state>, #142 <state>
Preflight: Docker <version>, Compose <version>, cgroup <driver> <version>, <cores> cores, <GB> GB available, 172.30/16 <free|used by …>
Gateway smoke test (§4.4): api <result>, admin <result>, worker <result>; gateway address <public|this host|proxy container>
HUMAN steps done: <list, with who approved>
Stack: <every service and its health>
Firewall: gateway reachable from ume-rag_llm: <HTTP code>
Seed: source <id>, sync job <id>, URLs <n>
Result: run <state> (added/updated/unchanged/failed), live chunks <n>, vector dims <n>, canary <recorded|not yet>
Open problems: <none | what failed, with the output>
```

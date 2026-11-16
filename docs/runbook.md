# Operational runbook

For the pilot host set up as in [production-setup.md](production-setup.md). Commands run from the directory that holds
`deploy/`, as root or a member of the `docker` group; `dc` below stands for `docker compose --env-file deploy/.env`.
The design behind this page is [`plans/platform/repo-and-deployment.md`](../plans/platform/repo-and-deployment.md)
§6 and §7.4. Upgrades are in the [upgrade guide](upgrade-guide.md), security events in
[security-events.md](security-events.md), the test host's deployment and drill in [test-host.md](test-host.md). Every port and outbound destination, per setup: [network-and-ports.md](network-and-ports.md).

## Ownership and targets

Name these before real data arrives (placeholders until then):

| Role | Who |
|---|---|
| Service owner | `<owner>` |
| On-call function | `<owner>` |
| Operations owner (reviews this runbook) | `<owner>` |
| Data protection officer (dataskyddsombud) | `<owner>` |
| Records owner (registrar) | `<owner>` |
| Gateway team (the gateway and the shared Keycloak) | `<owner>` |

Pilot targets: **RPO ≤ 5 minutes, RTO ≤ 4 hours** for the database; uploads copied off the host within 15 minutes.
Retention of the backups is 35 days (proposed).

## Everyday commands

```bash
dc ps                                    # state and health of every container
dc logs --since 1h rag-worker            # one service's log
dc restart rag-api                       # restart one service
dc stop                                  # stop everything, keep the volumes
dc up -d --wait                          # start (or apply a changed .env or image)
```

Never run `dc down --volumes` on a deployment: it deletes `postgres-data`, `rag-blobs`, the signatures and, with a
`local` backup target, the backup repository. A plain `dc down` keeps the volumes. Every .NET service answers
`/health/live`, `/health/ready` and `/version`; the published ones are on `https://localhost:8543` (api) and `:9543`
(admin), with the internal CA in `deploy/certs/ca.pem`.

## Daily checks

Until the metrics reach the municipal monitoring (how is still to be settled, *Metrics and alerts*), check by hand
each working day:

```bash
systemctl list-timers 'rag-*'                   # every timer has a next run; the backup ran last night
systemctl list-units --failed 'rag-*'           # no failed unit
cat /var/lib/ume-rag/backup.prom /var/lib/ume-rag/upload-backup.prom
dc ps                                           # every service healthy (rag-migrate exited 0)
sudo curl --cacert deploy/certs/ca.pem https://localhost:8543/health/ready
sudo curl --cacert deploy/certs/ca.pem https://localhost:9543/health/ready
dc exec -T postgres pgbackrest --stanza=ragdb info
```

In `backup.prom`: `rag_backup_age_seconds` under 93600 (26 h), `rag_backup_repo_status 0`,
`rag_backup_wal_archive_age_seconds` under 300, `rag_backup_last_run_success 1`, and
`rag_backup_status_timestamp_seconds` within the last 10 minutes. In `upload-backup.prom`:
`rag_upload_backup_last_success_timestamp_seconds` within the last 30 minutes (not with an S3 target). These are the
thresholds of the proposed alerts below; anything else: *Where to look first*.

## Reading Ume.Rag's cost beside the gateway's

Ume.Rag meters its own model calls so editors can see what each assistant and knowledge base costs, per step and
client. That is reporting only: **the gateway's usage report is the record for internal charging**, and the two never
agree to the cent. This section says where Ume.Rag's figures come from and names every reason they differ from the
gateway's for the same key and day. The design is `plans/platform/cost-attribution.md` §5 and admin design §8.6.

### Where the figures come from

Every model call that gets a `2xx` answer, and every streamed answer that breaks off, adds one call to a daily row, per
Europe/Stockholm day:

| Table | What it counts | Day |
|---|---|---|
| `chat."AssistantDailyUsage"` | every call of a chat turn, per assistant, client and step: `chat-answer`, `condense`, `condense-long`, `follow-ups`, `query-embed` (cache misses only), `rerank`; the test panel's calls under the client `admin-console` | the turn's start |
| `content."CollectionDailyUsage"` | the worker's calls per knowledge base and step: `embed` (indexing, re-embedding, preview), `eval-generate`, `eval-judge`, `eval-dedup`, and the retrieval evaluation's `query-embed` and `rerank` | the call's answer |

Each row holds calls, input tokens (cached ones included, as the gateway counts them), cached input tokens, output
tokens, and the cost in two parts that are never mixed:

- **From the AI gateway** (`CostUsd`): the gateway's own figure, from the `x-ume-cost-usd` header on a complete answer,
  or from the stream comment `: ume-cost-usd=<usd>` the gateway writes before `data: [DONE]` (rag-api asks for it with
  `x-ume-stream-cost: 1` and never passes it on).
- **Estimated** (`EstimatedCostUsd`, `EstimatedCalls`): a call that came back without a gateway cost, priced from the
  model list's mirrored prices: (input − cached) × input price + cached × cached price (the input price when that is
  empty) + output × output price, per million tokens, rounded to 8 decimals. A model without prices is estimated at 0.

A call answered with an error status adds nothing (the gateway charges no failed request). A `2xx` answer whose body
can't be used (rerank output that doesn't parse, a condense that fails its check) still counts, since it was charged.

Where to read them (each view shows the gateway part and the estimate apart):

- **Assistant → Statistics → Cost**: the last 30 days, 90 days or 12 months, per day or month, by step and by client
  (*Test panel* is `admin-console`). Anyone who can read the assistant sees it.
- **Knowledge base → Overview → Cost**: the last 30 days, split into *Indexing* (`embed`) and *Evaluation* (the other
  steps). Only the owning department sees it; a department the knowledge base is shared with does not.
- **Costs** (department and platform admins): any period by day (at most 400 days) or month (at most 36 months), per
  department, assistant or knowledge base and step, highest cost first, and **Download CSV**.

The CSV is one row per period, department, object, step and client, `;`-separated, UTF-8 with a BOM, costs with a
decimal comma as the gateway's own export:

```text
period;department;kind;object;step;client;calls;input_tokens;cached_input_tokens;output_tokens;cost_usd;estimated_cost_usd;estimated_calls
2026-11-12;Stadsbyggnad;assistant;Bygglov;chat-answer;Open WebUI pilot;409;1840500;612000;163600;1,8301;0,0105;3
```

`client` is empty for knowledge bases, a month period is `yyyy-MM`, and a deleted object shows as `<id> (deleted)`. A
row's department is the object's department when the call was made, so a moved object's earlier cost stays where it
was. The CSV names objects, not gateway keys: note which key each assistant and knowledge base runs as (its page shows
the key) before comparing with the gateway's export, which is per key.

### The gateway's figures beside Ume.Rag's

When rag-admin's gateway context names the Integration API (`integration_api_url`, the gateway's
`UME_INTEGRATION_API_URL`) and its credential key has the scope `usage.read`, the Cost card of an object **with a gateway key** reads that
key's usage from the gateway's Integration API (`/integration/v1/usage?groupBy=step&keyId=…`) for the same period:

- a **Gateway** column in the *By step* table: the gateway's cost for the key per `x-ume-step`;
- a line "The AI gateway counted … in … requests for the key." The requests include failed and cancelled ones, so
  compare Ume.Rag's calls with the gateway's successful requests, not with this figure;
- the note that the gateway's figures are the key's: if other assistants or knowledge bases share the key, their calls
  are in the gateway's column too (the note shows only when more than one assistant or knowledge base holds the key);
- the key's budgets (*Budgets in the AI gateway*) at the top of the assistant page and in the knowledge base's state
  card.

The gateway's rows are Europe/Stockholm days too, cached 5 minutes per key and period. A gateway row without a step
(another system using the key, or months older than the gateway's kept records, which have only monthly totals) is in
the line's total but in no step row. Ume.Rag never overwrites its own figures with the gateway's.

Without the read-back (the setting unset, the scope missing, the gateway refusing or rate-limiting), the card shows
**See the key's usage in the AI gateway** instead, a link to the gateway's usage page. The reason shows under
*Models*, in the *AI gateway* card's row *Cost and budgets from the AI gateway* (off, on, or
the last refusal with its time), and is logged:

```bash
dc logs --since 1h rag-admin | grep "Integration API"   # "refused or failed (HTTP 403, scope_missing); no read-back this time"
```

A platform admin also sees it in `integration.lastError` of `GET /api/gateway/status`; the admin UI's *AI gateway* card on *Models* shows it too.

### Why the totals differ

Compare the same key, the same day and Ume.Rag's gateway part plus its estimate. The differences, in the order they
usually matter:

1. **Calls Ume.Rag never sees answered.** rag-api retries a `502`/`504` once, and a connection failure once; the gateway
   has already tried its fallbacks before it answers `502`/`504`. The failed attempts are requests in the gateway
   (usually at no cost, since no provider answered) and nothing in Ume.Rag; a fallback inside the gateway is one
   request there, with its fallback count. A non-streamed call that Ume.Rag gives up on (a condense or follow-up
   timeout) records nothing in Ume.Rag, but the gateway accounts for what it had used. A worker job attempt that
   fails is counted in `CollectionDailyUsage`, but a crash between the job queue's write and the usage write, or a lost
   lease, loses that attempt's usage: an undercount, never a double count. These make the gateway's figure the higher.
2. **Estimated calls.** An estimate uses the model list's prices, which may lag the gateway's, and Ume.Rag's own token
   counts. The CSV's `estimated_calls` and `estimated_cost_usd` say how much of a figure is estimated.
3. **Aborted streams and cancelled turns.** When a user leaves, or the idle (`Chat:StreamIdleTimeout`) or total
   (`Chat:MaxAnswerDuration`) timeout cuts an answer, the stream ends without usage or cost comment. Ume.Rag records the
   turn (the user's message, the partial answer, the day's `Cancelled` count in `AssistantDailyTotals`) and the answer call at its prompt estimate
   as input, no output, estimated. The gateway accounts the same request as `ClientCancelled` (499) with its own
   figures, so the two estimates differ and the gateway counts it as a request but not a successful one.
4. **Calls only the gateway counts.**
   - Query embeddings of Knowledge API callers without an assistant (integration keys, token callers, MCP). They run as
     the first searched knowledge base's key, so they show in that knowledge base's **Gateway** column under
     *Query embedding* with nothing beside them in Ume.Rag.
   - The admin's own calls outside the test panel: the retrieval test (on the knowledge base's key), and the embedding
     canary, embedding profile test and status checks (on rag-admin's credential key).
5. **The test panel.** Its calls run as the assistant's key and are counted under the client `admin-console`, so they
   show as *Test panel*, apart from real use. rag-migrate creates that client (type `internal`, managed by the deployment)
   on every run; without it (rag-migrate has not run) nothing is written and rag-admin logs it:

   ```bash
   dc logs --since 24h rag-admin | grep "Test panel usage"   # "Test panel usage of assistant <id> not written (<n> calls): no admin-console client row of kind internal"
   ```

6. **Days.** Ume.Rag's days are Europe/Stockholm days: a chat turn is dated when it starts, a worker call when it is
   answered. The gateway dates a usage record when the request ends. A turn that starts just before midnight can land
   on the next day in the gateway. Which day the gateway's views use (gateway v0.10.1):
   - the Integration API (the Cost card's **Gateway** column) and the Usage dashboard's day buckets: Europe/Stockholm;
   - the date range of the usage page, the request list and the gateway's CSV export: midnight in the browser's time
     zone, so read them on a computer set to Swedish time;
   - the usage summary grouped by *day*: grouped on the record's timestamp date, not on Stockholm days, so its day
     boundary can be an hour or two off. Don't use it to reconcile a day.
7. **Objects without a key.** Their calls run as the service's credential key (rag-api's for chat, rag-worker's for
   indexing, rag-admin's for the test panel), so in the gateway they are part of that key's line together with every
   other object without a key. The Cost card has no **Gateway** column for them. The same holds for every object
   until the gateway's `features` contain `act-as-key`.
8. **Rounding.** The stream comment carries six decimals; sums over thousands of calls can differ in the fourth
   decimal.

An ingestion run's own cost (`Runs.CostUsd`) covers only the attempt that wrote each document, so it can be lower than
the knowledge base's `embed` row and the gateway's for the same day; reconcile with the daily row, not the run.

### Matching per step and per run

Every call carries `x-ume-step` (the step names above) and `x-ume-run-id`, which the gateway stores on its usage record
(gateway phase 2, `features` `run-metadata`):

| Calls | `x-ume-run-id` |
|---|---|
| a chat turn's calls | the turn's message id: the answer's `chatcmpl-<id>` with the hyphens put back |
| an ingestion run's calls | the run's id (the address of its run page, `/runs/<id>`) |
| an evaluation run's calls | the evaluation run's id |
| set generation and preview | the job's id |

- **Per step:** the Cost card's **Gateway** column is the gateway's sum per `x-ume-step` for the key and period; set it
  beside Ume.Rag's row of the same step.
- **Per run or turn:** in the gateway's usage page, filter the request list by the key and a time range around the run
  or turn, and open a request: its *Run* row shows the run id and step. At gateway v0.10.1 the request list has no
  filter or column for the run, so for a large run compare per step and day instead. The Integration API's `runId=`
  filter exists, but Ume.Rag's admin does not ask it.

### What a rising estimated share means

`rag.chat.cost` (USD) counts every chat call's cost by `step` and `source`: `gateway` or `estimate`. Normally almost all
of it is `gateway`; the estimate is the aborted streams. When the `estimate` share rises:

- by step `chat-answer` only, with more cancelled turns (`AssistantDailyTotals.Cancelled`): users leave or answers hit
  the idle or total timeout. Look at the model's speed in the gateway;
- on every step at once: the gateway stopped sending its cost. Check that the gateway is at a release with the stream
  cost and `x-ume-cost-usd`, and that nothing between rag-api and the gateway strips the header;
- an estimated cost of 0 for calls that have tokens: the model has no prices in the model list (*Models*, filled by the model sync).

`rag.chat.tokens` counts the tokens by `direction` (`input`, `cached-input`, `output`) and `step`. For knowledge bases,
`rag.ingestion.cost` (USD) counts every worker call's cost by job `class` and `source` in the same way, and
`rag.embed.tokens` the input tokens of its embedding calls; every worker call has a gateway cost, so any `estimate`
there means the gateway stopped sending `x-ume-cost-usd`.

### A worked example

One day, 2026-11-12, of the department Stadsbyggnad: two assistants, *Bygglov* (key `bygg-chat`) and *Planfrågor* (key
`plan-chat`), and one sync of the knowledge base *Bygglov webb* (key `bygg-kb`). The operator downloads the Costs CSV for
that day, opens each object's Cost card, and in the gateway reads the usage per key for 2026-11-12 (Integration API
column or the usage page with the browser in Swedish time).

| Key | Ume.Rag (gateway part + estimate) | Gateway | Difference and why |
|---|---|---|---|
| `bygg-chat` | 1.9859 + 0.0105 = 1.9964 USD, 1,327 calls (3 estimated) | 2.0012 USD, 1,336 requests, 1,327 successful | +0.0026 the gateway's own figures for the 3 aborted answers (item 3); +0.0004 two follow-ups that timed out in rag-api (item 1); +0.0018 a turn started at 23:59:58 on 11 November, whose calls the gateway dates the 12th (item 6). Requests: 4 retried `502`s, 3 cancelled answers, 2 cancelled follow-ups (items 1, 3) |
| `plan-chat` | 0.6120 + 0 = 0.6120 USD, 402 calls | 0.6330 USD, 407 successful | +0.0210 five test-panel calls not written; rag-admin logged "Test panel usage of assistant … not written (5 calls)", there is no `admin-console` client row (item 5) |
| `bygg-kb` | `embed` 0.0820 + 0 USD, 530 calls | `embed` 0.0820 USD; *Query embedding* 0.0035 USD | `embed` agrees, the 530 calls include 12 documents' first attempts that failed and were retried (item 1; the run's own `Runs.CostUsd` is 0.0790). The 0.0035 are query embeddings of Knowledge API callers without an assistant and two retrieval tests (item 4) |

Every difference has a name, so the day is reconciled. A difference with no name, or one that grows from day to day on
the gateway part (not the estimate), is worth an issue: Ume.Rag may be missing a call it should count.

## Backups

### What runs

| Unit | Runs | When |
|---|---|---|
| `rag-backup.timer` | `backup-postgres.sh --type auto`: a pgBackRest backup inside the postgres container; full on Sundays or when the newest full is 7 days old (or there is none), else differential; expiry follows each backup | daily at 01:30 (up to 15 minutes later), caught up after downtime |
| `rag-backup-status.timer` | `backup-postgres.sh --status-only`: the metrics below, from `pgbackrest info` and `pg_stat_archiver` | every 2 minutes |
| `rag-upload-backup.timer` | `backup-uploads.sh`: new files below `uploads/` of the blob store to the upload copy, never overwriting | every 10 minutes (not with an S3 target) |
| `archive_command` in postgres | `pgbackrest archive-push`: every WAL segment, synchronously | at least every 60 s (`archive_timeout`) when anything was written |

Derived blobs (fetched pages, parse outputs) are not backed up: a restore fetches and parses them again. The pepper
(`deploy/secrets/.pepper`), `deploy/certs/data-protection.pfx` and the rest of `deploy/secrets/` are backed up
separately, through the municipality's secret store; the blob master key and the pgBackRest passphrase are also
escrowed (*Key escrow*). Open WebUI's data directory (`UME_RAG_OPENWEBUI_DATA_DIR`) is **not backed up by any script
in P0** (*Public-records requests*).

```bash
systemctl list-timers 'rag-*'
journalctl -u rag-backup.service --since yesterday
dc exec -T postgres pgbackrest --stanza=ragdb info      # backup sets and the archived WAL range
```

### `backup-postgres.sh`

```bash
sudo deploy/backup-postgres.sh --type full               # by hand: full, diff or auto
sudo deploy/backup-postgres.sh --type full --start-fast  # an immediate checkpoint instead of a spread one (drills)
sudo deploy/backup-postgres.sh --status-only             # refresh the metrics only
sudo systemctl start rag-backup.service                  # the scheduled run (--type auto), now
```

`--env-file FILE` (default `deploy/.env`) picks the deployment, and `--state-dir DIR` (default `$STATE_DIRECTORY`, else
`/var/lib/ume-rag`) where `backup.prom` and `backup-last-run` go. A diff without a full becomes a full. A failed run is
recorded (`rag_backup_last_run_success 0`) and the metrics are refreshed if the database still answers.

### Metrics and alerts

The scripts write Prometheus text files to `/var/lib/ume-rag/` (`backup.prom`, `upload-backup.prom`), atomically and
world-readable. Point node_exporter's textfile collector, or the collector that reads them, at that directory (how they
reach the municipal monitoring is still to be settled). Every value is read from the repository and `pg_stat_archiver`
at run time, so a backup taken by any means is reported.

| Metric | Meaning |
|---|---|
| `rag_backup_age_seconds` | since the end of the newest backup of any type |
| `rag_backup_last_timestamp_seconds{type}` | end of the newest `full`, `diff` and `incr` backup (0: none) |
| `rag_backup_repo_status` | pgBackRest's stanza status code: 0 ok, 2 no valid backup, 99 the repository could not be read |
| `rag_backup_wal_archive_age_seconds` | since the newest archived WAL while unarchived WAL exists; 0 when everything written is archived |
| `rag_backup_wal_last_archived_timestamp_seconds`, `rag_backup_wal_archive_failed_total`, `rag_backup_wal_last_failed_timestamp_seconds` | from `pg_stat_archiver` |
| `rag_backup_last_run_success`, `rag_backup_last_run_timestamp_seconds` | the outcome of the last scheduled backup |
| `rag_backup_status_timestamp_seconds` | when these values were read |
| `rag_upload_backup_last_success_timestamp_seconds`, `rag_upload_backup_copied`, `rag_upload_backup_files` | the upload copy: last success, files copied by the last run, files in the copy |

Proposed alerts (§7.4 rule 5):

- `rag_backup_age_seconds > 93600`: no backup for 26 hours;
- `rag-backup.service` failed, or `rag_backup_last_run_success == 0`;
- `rag_backup_wal_archive_age_seconds > 300` for 2 minutes: the newest archived WAL is older than the 5-minute RPO.
  With `archive_timeout` 60 s a busy server reads at most about 60 s; the 2 minutes absorb the first write after an
  idle spell;
- `rag_backup_repo_status != 0`, or `rag_backup_status_timestamp_seconds` older than 10 minutes (the status timer
  stopped);
- `rag_upload_backup_last_success_timestamp_seconds` older than 30 minutes.

## Restore (point in time)

`deploy/restore-pitr.sh` restores ragdb **in place** to a moment you choose. It replaces the current database:
everything archived before it starts stays restorable, nothing written after its first step is.

**First, the setup wizard's re-open value:** empty `UME_RAG_SETUP_REOPEN` in `deploy/.env` (Kubernetes: the
`Setup__Reopen` literal; Portainer: the variable). The restored database may have consumed an older value, and rag-admin
would open the wizard again for an hour. The client secrets the wizard stored need the same `data-protection.pfx`
(*Sign-in and the setup wizard*).

1. **Choose the time.** Find the last good moment (an incident report, the audit log). Check that it lies inside the
   archived range with `dc exec -T postgres pgbackrest --stanza=ragdb info`. Write it with a UTC offset; without one the
   host's time zone applies. Transactions committed up to and including that time are kept.
2. **Preview.** Without `--confirm` the script prints what it would do and stops:

   ```bash
   sudo deploy/restore-pitr.sh --target-time '2026-10-30 21:15:00+01:00'
   ```

   It names the Compose project, the target time, the backup set (`--set LABEL` picks one instead of the newest that
   ended before the target) and the services it will stop.
3. **Restore**, in an announced maintenance window:

   ```bash
   sudo deploy/restore-pitr.sh --target-time '2026-10-30 21:15:00+01:00' --confirm
   ```

   It then:
   1. closes the current WAL segment and waits up to 60 s until it is archived, so the restore can reach the newest
      commits;
   2. stops `rag-api`, `rag-admin` and `rag-worker` (those that run, the second slots of `--rolling` included), then
      postgres;
   3. runs `pgbackrest restore --type=time --delta` in a one-off container of the postgres service (same image, user,
      volumes and repository settings) on the `postgres-data` volume;
   4. starts postgres, which recovers to the target and pauses there, and prints the last transaction it replayed;
   5. promotes it to a new timeline (the old one stays in the repository) and runs `pgbackrest check`;
   6. starts the services it stopped, waits until they are healthy, and prints the time of each step and the total
      (the achieved RTO).

   `--timeout SECONDS` (default 10800) bounds the recovery. If the repository has no WAL up to the target, it stops
   with "the repository has no WAL up to …": pick an earlier time. `--env-file FILE` picks another deployment.
4. **Check the data** (the drill's checks below), then **take a full backup** at once, so the new timeline has its own
   base:

   ```bash
   sudo deploy/backup-postgres.sh --type full
   ```

5. **Blobs.** Blobs are write-once and purged only after the backup retention, so the restored database finds every
   blob it references. The worker's orphan audit (§7.4 rule 2) then reports blobs without a referencing row (left to
   the purge) and documents whose derived blob is missing (fetched or parsed again on the next sync). The audit is
   not built yet. A missing **upload** is restored from the upload copy
   (*Upload copy*).

**Timings.** The local drill (PR #40; a 30 MB database, local repository) took **23.0 s** end to end: archive and stop
2.1 s, restore 0.9 s, recovery to the target 2.9 s, promote 1.6 s, check 0.3 s, services healthy 15.2 s. A second
restore on the new timeline took 22.7 s. Restore and recovery grow with the database size and the WAL since the chosen
backup, and a share or S3 target is slower than a local volume: the test-host drill measures realistic numbers.

### Restoring onto another host

Not drilled yet; the test-host drill confirms it. The order matters, because `init-deployment.sh` never overwrites
existing files:

1. Before running `init-deployment.sh` on the new host, put back from the escrow and the secret store:
   `secrets/pgbackrest/cipher-pass` (owner 999, mode 0600), `secrets/keys/rag-blob-internal` (owner 1654, mode 0400),
   `secrets/.pepper` and `certs/data-protection.pfx`. The escrow set's `MANIFEST` says where each key goes.
2. Run `init-deployment.sh` with the same `--backup-target`, then the install steps of production-setup.md up to
   `up --wait` (no `stanza-create`: the stanza exists).
3. Run `restore-pitr.sh --target-time … --confirm`, then take a full backup.

For a drill (not a real loss of the production host), give the drill host a **copy** of the repository, not the
production repository itself: after the restore it archives WAL and its backup timer takes backups, which must not
land in production's repository.

## Restore drill

§7.4 rule 3: a drill after the first deployment, then **every quarter**. The P0 drill was run locally (PR #40, timings
above). The **first drill on the test host is scheduled before go-live (P1b)**, together with the test-host deployment
(decisions table, "M2 and test hosting"); it is still open, and its specifics are in [test-host.md](test-host.md).
Record the date, the achieved RPO and RTO, and anything that went wrong.

1. **Isolated host** with its own **copy** of the pgBackRest repository (never production's) and of the blob volume
   (or the upload copy), set up as in *Restoring onto another host*, from the **escrowed** keys: this is the check
   that the escrow can be read back. Once the keys are in place, run `escrow-keys.sh --verify` against the escrow set:
   it must print `PASS`.
2. **Choose a target time** that tests the RPO: a moment a few minutes before the drill, and note the newest change
   you expect to survive.
3. **Restore** with `restore-pitr.sh --target-time … --confirm`; keep its timing output (the RTO).
4. **Check:**
   - the newest expected change is there and nothing after the target is;
   - `pgbackrest check` passed (the script runs it) and every service is healthy;
   - a search query returns results with citations;
   - a document preview opens (an `internal` document: this reads a blob with the escrowed key);
   - a chat history read: metadata only while the pilot runs history mode `none`.
5. **Take a full backup** on the restored host if it stays in use; otherwise remove it and its copy.
6. **Record** the RPO (target time minus the newest change that survived, against 5 minutes) and the RTO (against 4
   hours) in the operations log.

## Key escrow

`deploy/escrow-keys.sh` exports the keys no backup can replace to escrow media and reads them back. Escrowed in P0
and P1:

- `secrets/keys/rag-blob-*`: the blob master keys of the file key provider (`rag-blob-internal`). Without them the
  `internal` blobs cannot be decrypted.
- `secrets/pgbackrest/cipher-pass`: the repository passphrase. Without it no backup can be read.

Chat master keys (`rag-chat-<level>`) join only once stored chat content is allowed (register A1); there are none in
P0 and P1. The pepper and `data-protection.pfx` go to the secret store, not to this escrow.

```bash
sudo deploy/escrow-keys.sh /media/escrow-a              # writes /media/escrow-a/ume-rag-keys-<project>-<UTC time>/
sudo deploy/escrow-keys.sh --verify /media/escrow-a     # the newest set there, or name one set
```

The export writes the keys with `SHA256SUMS` and a `MANIFEST` (what each key is and where it goes back), read-only for
the owner. `--verify` checks the checksums and the key formats, and that every key equals this deployment's current
one (and the passphrase equals `repo1-cipher-pass` in `repo.conf`); it prints `PASS` and exits 0, or names each
`FAIL`. `--env-file FILE` or `--secrets-dir DIR` points it at another deployment.

- **Run it** after the first deployment (production-setup.md) and **after every rotation**, then verify: `--verify`
  fails with "differs from the deployment's key" until the new key is escrowed. The upgrade guide verifies the escrow
  before each upgrade.
- **Seal the media in two places** (or use the key service's own backup). The files are in the clear: whoever holds
  the media can read every `internal` blob and every backup. Where the escrow lives is agreed with IT (to-verify #145).
- **Keep each set as long as the data and backups it protects.**
- **A restore reads them back**: copy the keys to the places the `MANIFEST` names (*Restoring onto another host*),
  then `--verify` against the set; every drill does this.

## Upload copy

`rag-upload-backup.timer` runs `deploy/backup-uploads.sh` every 10 minutes, so an upload is off the host within 15. It
runs the profiled Compose service `rag-upload-backup` (the postgres image's shell as uid 1654, the blob volume
read-only, no network) with `UME_RAG_UPLOAD_BACKUP_DIR` mounted as the target. Each new file is written under a
temporary name and renamed; a file already in the copy is skipped. Deletions are not propagated: prune the copy with
the backup retention (35 days). With an S3 target there is no upload copy yet (not built in P0).

```bash
sudo deploy/backup-uploads.sh          # by hand; --env-file FILE, --state-dir DIR
sudo systemctl start rag-upload-backup.service
journalctl -u rag-upload-backup.service --since today
```

### Restoring a missing upload

The upload copy holds every file below `uploads/` of the blob store, under the same key (`<backup target>/uploads/uploads/`,
or `deploy/upload-backup/uploads/` with a `local` target). Keys are write-once, so copying back only what is missing is
always safe. With the stack running:

```bash
dir="$(sed -n 's/^UME_RAG_UPLOAD_BACKUP_DIR=//p' deploy/.env)"
docker run --rm --network none --user 1654:1654 --read-only \
  -v ume-rag_rag-blobs:/data/blobs -v "$dir/uploads:/backup/uploads:ro" \
  --entrypoint sh ume-rag/postgres:latest -c '
    cd /backup/uploads && find . -type f ! -name "*.partial" | while IFS= read -r f; do
      [ -e "/data/blobs/rag/uploads/$f" ] && continue
      mkdir -p "/data/blobs/rag/uploads/$(dirname "$f")" && cp -p "$f" "/data/blobs/rag/uploads/$f"
    done'
```

Use the deployment's `POSTGRES_IMAGE` if it is pinned (any image with a POSIX shell works). This step has not been
drilled yet.

## Updates and restarts

Upgrades follow the [upgrade guide](upgrade-guide.md): announce the window, take a full backup and verify the escrow,
replace `deploy/`, run the superuser steps if the release lists them, then `dc up -d --wait`, which runs `rag-migrate`
first. With one slot each (the default) `rag-api` and `rag-admin` are replaced together in one announced window. With
`init-deployment.sh --rolling` there are two of each behind the proxy, and `deploy/rolling-update.sh` replaces them one
at a time for releases marked *Rolling update: yes* ([Updates without downtime](zero-downtime-updates.md)).

**The worker is stop-then-start, never overlapping.** Two `rag-worker` processes at once would exceed its role's
connection limit; its job leases make a short pause harmless. Never scale it (`--scale rag-worker=2`) or start a
second worker against the same database. To restart it by hand:

```bash
dc stop rag-worker              # it gets its stop grace period (120 s) before it is killed
dc up -d --wait rag-worker
```

`dc restart rag-worker` also stops it before it starts. The parser, ClamAV (when its `clamav` profile is on) and the
renderer (when its `render` profile is on) can be restarted on their own: the worker reports them in its own health and retries (a render that loses the renderer fails as
`render.unavailable` and keeps the page's last good content).

**After any change to the gateway's models** (a new deployment behind an alias, a provider switch, a gateway upgrade),
a platform admin runs **Check embedding canary** on *Models*. The platform makes no model calls of its
own to notice a changed embedding model: the check embeds each active profile's canary text once and stores the
similarity on the profile. Below 0.999 the worker refuses the profile until a later check passes (*Where to look
first*).

### Restarting unhealthy containers

Docker restarts a container only when its main process exits. ClamAV's does not, so a hung clamd would stay
unhealthy. `rag-restart-unhealthy.timer` runs `deploy/host/restart-unhealthy.sh` every minute: it restarts every
container labelled `ume-rag.restart-unhealthy=true` (today `rag-clamav` and `rag-renderer`, where they run) that Docker reports
unhealthy. A container
in its start period counts as starting, not unhealthy, so a slow first download does not loop.

```bash
deploy/host/restart-unhealthy.sh --dry-run      # what it would restart now
journalctl -u rag-restart-unhealthy.service --since today
```

## Portainer and other hosts without shell access

`deploy/portainer/compose.portainer.yaml` runs the gateway and Ume Rag as **one** Portainer stack, configured only
through environment variables, for hosts where `init-deployment.sh` cannot run. It is the gateway's
`deploy/compose.portainer.yaml`, unchanged but for the default of its `UME_INTEGRATION_MANIFEST` (below), plus RAG's
services (all named `rag-…`): rag-postgres, rag-migrate, rag-api (8543), rag-admin (9543), rag-worker, rag-parser and,
with their profiles, rag-clamav (`clamav`) and rag-renderer (`render`) and rag-openwebui (`openwebui`, #657). Step by
step for a new VM or an upgrade: [portainer.md](portainer.md).

**Deploy.** In the gateway's existing stack, *Editor*: replace the file with this one, keep the stack name (the
gateway's volumes `<stack>_ume-certs`, `<stack>_ume-secrets` and its database are found by it), add the variables
below, and *Update the stack*. The RAG images on GHCR are private: add `ghcr.io` under *Registries* with a GitHub token
that can read packages.

| Variable | |
|---|---|
| `UME_DOMAIN`, `UME_RAG_API_PUBLIC_URL` | `UME_DOMAIN` is required, and the one value a new stack types (without it, `UME_GATEWAY_HOST` and `UME_RAG_API_HOST`): the host names default to `llm.<domain>` and `llm-admin.<domain>` (the gateway's), `rag.<domain>` (rag-api), `rag-admin.<domain>` and `chat.<domain>` (Open WebUI); `UME_GATEWAY_HOST`, `UME_ADMIN_HOST`, `UME_RAG_API_HOST`, `UME_RAG_ADMIN_HOST` and `UME_RAG_OPENWEBUI_HOST` override one each. rag-api's public URL is `https://<host>` unless `UME_RAG_API_PUBLIC_URL` gives another (a port: `https://rag.example.se:8543`) |
| `UME_INTEGRATION_MANIFEST` | the gateway's; unset, the stack file's default, `deploy/portainer/integration-manifest.json`: the gateway creates the integration *Ume.Rag* and its three credential keys (in a team and department `ume-rag` that a gateway admin can move) and `rag-bootstrap` gives each service its key. Set, your own manifest wins; set and empty, nothing is created |
| `UME_RAG_API_GATEWAY_KEY`, `UME_RAG_ADMIN_GATEWAY_KEY`, `UME_RAG_WORKER_GATEWAY_KEY` | optional: each service's credential key of the gateway integration *Ume.Rag*, issued by a gateway admin as [production-setup.md](production-setup.md), *Gateway credential keys*, says (an empty manifest, or a gateway elsewhere); set, it wins over the manifest's. Without a key that service's model calls are refused |
| `COMPOSE_PROFILES` | `clamav`: `rag-clamav`, the malware scan; `clamav,render` adds the renderer; `openwebui` the chat. `rag-bootstrap` sets the worker's malware scan from it: on with `clamav`, else off, which stores files without a scan, marked so ([production-setup.md](production-setup.md), *Malware scan*) |
| `UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN` | optional: roles come from the token's `roles` claim (Keycloak: the realm role `rag-platform-admin`), and a group grants one only for an identity provider whose tokens carry groups instead: this is the break-glass for the first platform admin, and every other role's groups are set in RAG's admin (*Settings → Role groups*) |

Sign-in is no variable: after the first start a gateway admin sets the gateway's in its own setup wizard
(`https://<gateway admin host>/setup`, with the setup code from `adminapi`'s log), then a platform admin sets
rag-admin's in rag-admin's wizard (`https://<admin host>/setup`, with the setup code from rag-admin's log; *Sign-in and
the setup wizard* below), which suggests the provider saved in the gateway's. A stack may declare sign-in instead: the
gateway's `UME_OIDC_AUTHORITY` is the provider of both (rag-admin's client `ume-rag-admin` uses it unless
`UME_RAG_OIDC_AUTHORITY` names another) and keeps the gateway's wizard closed, and with `UME_RAG_OIDC_CLIENT_SECRET`
(and the other `UME_RAG_OIDC_*`) rag-admin's wizard never opens ([portainer.md](portainer.md), *Declaring sign-in
instead*).

The optional ones (ports, a separate admin host, `UME_RAG_OIDC_TENANT_ID` for Entra ID, image overrides
`RAG_*_IMAGE`) are listed at the top of the file. The embedding profile is no variable: a platform admin creates it in
the admin UI from the gateway's embedding model (*Models → Embedding profiles → Create profile*; every RAG credential
key must allow that model). rag-api, rag-admin and rag-worker join the gateway's `llm`
network and reach it at `https://gateway:8443`, trusting the gateway's internal CA; rag-admin's organisation and model
syncs call it there with `UME_RAG_ADMIN_GATEWAY_KEY` (scope `organisation.read`). The gateway's part of the file is
the gateway's own, unchanged but for that one default.

**Bootstrap.** The one-shot `rag-bootstrap` runs after the gateway's `bootstrap` and creates RAG's internal CA, service
certificates, the Data Protection certificate, database passwords, the key pepper, the api's keys and the blob master
key in the `rag-certs` and `rag-secrets` volumes. It is idempotent: secrets are never regenerated, service
certificates are re-issued when they expire within 30 days or the host names change (its checklist then says which
services to restart, and names every certificate within 60 days of expiry:
[portainer.md](portainer.md#internal-certificates)). The secrets that follow a variable (the gateway credential keys,
a declared OIDC client secret) are written again on every deploy, and removed when the variable is emptied; they live in
the stack's environment, so restrict who can view it. Update the stack after changing one: the services pick up a
changed gateway key or client secret within seconds, without a restart.

**Differences from `compose.prod.yaml`.** No fixed subnets and no host firewall (*Host firewall* does not apply;
firewall 8543 and 9543 to your proxy yourself, or use the stack's own TLS proxy, the profile `proxy`:
[portainer.md](portainer.md#tls-proxy)), no ingestion slice, and no pgBackRest: WAL archiving is off, so back
up `rag-postgres` with `pg_dump` or volume snapshots. The renderer is off by default: Portainer cannot load its
seccomp profile from a file, so with `render` on it runs without Docker's seccomp filter (Chromium's own sandbox stays
on); without it the crawler indexes pages as served. Certificates from your PKI go on the stack's TLS proxy; the services' own
listeners keep the internal CA.

- **Back up** `rag-secrets/.pepper`, `rag-secrets/keys/rag-blob-internal` and `rag-certs/data-protection.pfx` with the
  database (for example `docker run --rm -v <stack>_rag-secrets:/s alpine cat /s/.pepper`). A database restored without
  them is unusable.
- **Upgrade.** Read the [upgrade guide](upgrade-guide.md), paste the new release's `compose.portainer.yaml` asset over
  the stack's file (one release at a time: [portainer.md](portainer.md)), and
  *Update the stack* with *Re-pull image and redeploy*. `rag-migrate` runs before the services.
- **Generated file.** Edit `deploy/portainer/compose.rag.template.yaml` or `deploy/portainer/bootstrap.sh`, then run
  `deploy/portainer/build-stack.sh`: it takes the gateway's current `compose.portainer.yaml` (from GitHub, or a path
  argument), adds RAG's sections and inlines the postgres config files. Run it again after a gateway release too.

## Host firewall

`deploy/host/docker-user-firewall.sh` gives each Compose network its egress rules in the `DOCKER-USER` chain
(`UME-RAG-EGRESS`) and rejects new connections from the networks to the host itself (`UME-RAG-INPUT`).
`rag-firewall.service` applies it when Docker starts or restarts; its timer applies it again every 15 minutes, because
the allow-lists are host names, resolved when it runs.

| Network | May reach |
|---|---|
| `edge`, `data`, `ingestion`, `parser`, `render` | nothing (`edge` carries the published ports only); `render` holds the renderer's Chromium, and the host is rejected for it too (`UME-RAG-INPUT`) |
| `llm` (api, admin) | the gateway, the gateway's Admin API (with `--allow llm=HOST:PORT`), the OIDC authority (`UME_RAG_OIDC_AUTHORITY`: empty, the setup wizard can't reach any provider), the Open WebUI host, the OTLP endpoint, DNS |
| `crawl` (worker) | the gateway, the OTLP endpoint, DNS, `UME_RAG_CRAWL_ALLOW`; then the public Internet, but no private, loopback, link-local or reserved range |
| `mirror` (clamav) | `database.clamav.net:443`, or only freshclam's proxy; unused with `--clamav off` |
| `backup` (postgres) | the S3 endpoint, DNS; nothing with a mounted share |
| `openwebui` | `rag-api`'s public URL and the OIDC provider, DNS (when `UME_RAG_OPENWEBUI_HOST` is set) |

A container sends all its egress through the network that holds its default route; Compose pins that with
`gw_priority` (api and admin: `llm`, worker: `crawl`).

```bash
deploy/host/docker-user-firewall.sh --dry-run                    # the rules it would apply (no root needed)
deploy/host/docker-user-firewall.sh --print-subnets              # the networks and their subnets
sudo systemctl start rag-firewall.service                        # apply now (after a change of deploy/.env)
sudo iptables -S UME-RAG-EGRESS
sudo deploy/host/docker-user-firewall.sh --remove                # take the chains and jumps out
```

Other options: `--env-file FILE`, `--subnet NAME=CIDR` (when a network's subnet differs from the Compose files),
`--allow NAME=HOST[:PORT]` (one more destination; port 443 when omitted) and `--dns IP` (instead of the resolvers in
`/etc/resolv.conf`). The timer runs the script without them, so an option given by hand lasts only until the next
run. To keep one, override the service's command with `sudo systemctl edit rag-firewall.service`:

```ini
[Service]
ExecStart=
ExecStart=/opt/ume-rag/deploy/host/docker-user-firewall.sh --env-file /opt/ume-rag/deploy/.env --allow crawl=intranet.umea.se
```

(with your deployment's path). `install.sh` leaves that override in place.

## Crawl politeness per host

By default the crawler is polite: 1 request a second and 1 connection per external host (10 and 4 on the municipal
hosts in `Settings.Crawl.MunicipalHosts`), robots.txt and its `Crawl-delay` respected, slower when the site is slow,
back-off on 429/503, a 30-minute pause when half of the last 100 answers fail, and scheduled crawls only in the source's
night window (connectors design §5.1, §6.3–§6.6). A platform admin can change that **per host** in the admin UI:
*Settings → Website fetching → Host politeness*. Each row is one host name (no scheme or port) with a
preset (`polite`, `municipal`, `fast`, `custom`) and the settings that override it; it is stored in
`Settings.Crawl.HostOverrides`, checked on save (V37, `crawl_politeness_invalid`) and audited as `settings.update`.

```json
"hostOverrides": {
  "www.umea.se": { "preset": "fast", "requestsPerSecond": 20, "circuitBreaker": "off",
                   "robots": { "mode": "ignore", "reason": "own site, web team consent 2026-11-10" },
                   "userAgent": "Mozilla/5.0 (X11; Linux x86_64)" }
}
```

- `fast` = 10 requests a second, 4 connections, `Crawl-delay` and latency ignored, any time of day.
- robots.txt can only be ignored per host and with a reason; its `Sitemap:` lines are still read. The User-Agent is
  free per host; robots.txt rules are still matched against the crawler's own product token.
- The crawler's own User-Agent is `Crawl:UserAgent` on `rag-worker` (default `UmeaKommunBot/1.0`), e.g.
  `Crawl__UserAgent="ExampleBot/1.0 (+https://www.example.org/bot)"`. Its product token, the part before the first `/` or
  space (letters, `_` and `-` only), is the name robots.txt groups, meta robots and `X-Robots-Tag` are matched against.
  The worker refuses to start with an invalid value.
- The deployment caps every host at `Crawl:MaxHostRate` (50 requests a second) and `Crawl:MaxHostConnections` (16).
  Set them alike on `rag-admin` (which refuses more) and `rag-worker` (which clamps to them), e.g.
  `Crawl__MaxHostRate=20` in both services' environment. `renderPerHost` is capped at `Render:MaxConcurrent` (4) and, in effect, at the host's `maxConnections`.
- A crawl run's page lists the hosts it fetched with their own politeness and the robots.txt reason.

## Public-records requests (allmän handling)

**Status: open; there is no admin procedure yet.** In the pilot our database holds no chat content (history mode
`none`), so a request for a conversation is answered from Open WebUI's own copy, the primary copy (weakness review §4,
plan §8). With the pilot settings in `deploy/compose.openwebui.yaml` (`ENABLE_ADMIN_CHAT_ACCESS=false`,
`ENABLE_ADMIN_EXPORT=false`) **no Open WebUI admin can find, open or export another user's conversation**. The only
route that works today is the user's own export. Which route the registrar uses is decided in the DPIA (register G1,
with W2; to-verify #173) before go-live; direct SQL against Open WebUI's database is ruled out. This section gets the
procedure once G1 has decided it.

What the pinned Open WebUI (v0.12.5) does, read from its source at tag `v0.12.5` (paths below `backend/open_webui/`
unless they start with `src/`):

| Setting or route | Effect at v0.12.5 | Source |
|---|---|---|
| `ENABLE_ADMIN_CHAT_ACCESS` | read once from the environment at start (default `True`; we set `false`), not a persistent setting: changing it means recreating the container | `env.py:371` |
| An admin lists a user's chats: `GET /api/v1/chats/list/user/{user_id}`, the *Chats* button in *Admin > Users* | refused (401) with the flag off; the button is hidden (and never shown for admin-role users) | `routers/chats.py:751-764`; `src/lib/components/admin/Users/UserList.svelte:438` |
| An admin opens a chat by id: `GET /api/v1/chats/{id}`, and the admin fallback of `GET /api/v1/chats/share/{id}` | only the owner can read it with the flag off | `routers/chats.py:1380-1390`, `:1276-1278`; `models/chats.py:2001-2006` |
| `ENABLE_ADMIN_EXPORT` | read once from the environment (default `True`; we set `false`) | `config.py:2150` |
| All chats of all users `GET /api/v1/chats/all/db`, the raw database `GET /api/v1/utils/db/download`, the export section of *Admin > Settings > Database* | refused (401) with the flag off; the section is hidden | `routers/chats.py:1081-1085`; `routers/utils.py:74-78`; `src/lib/components/admin/Settings/Database.svelte:127` |
| The user's own export: `GET /api/v1/chats/all` (only the caller's chats), the chat export under *Data Controls* in the user's settings, and *Download > Export chat (.json)* in a chat's menu | works; the menu entry needs `permissions.chat.export`, default `True` (`USER_PERMISSIONS_CHAT_EXPORT`, which we do not set) | `routers/chats.py:1040-1045`; `src/lib/components/chat/Settings/DataControls.svelte:93-97`, `:185-196`; `src/lib/components/layout/Navbar/Menu.svelte:427-446`; `config.py:1933`, `:2045` |
| Feedback snapshots: an admin reads any feedback with its `snapshot.chat` (`GET /api/v1/evaluations/feedback/{id}`, `GET /api/v1/evaluations/feedbacks/all/export`) | works, but only for rated messages, and only the chat as it was when rated | `routers/evaluations.py:449-459`, `:354-364`; `models/feedbacks.py:92-93` |
| The feedback service account's API key | reaches only the allow-listed paths (prefix match), so it cannot read chats | `utils/auth.py:538-546` |
| The feedback service account itself | created as the store's first user, so as admin, from `WEBUI_ADMIN_EMAIL` and `WEBUI_ADMIN_PASSWORD` when Open WebUI starts with no users; with `ENABLE_PASSWORD_AUTH=false` (our default) it cannot sign in, and `ENABLE_LOGIN_FORM=false` only hides the form | `main.py:399-403`; `utils/auth.py:631-662`; `routers/auths.py:663-674`; `config.py:1704-1708` |
| A chat the user deletes | is removed from the database (rows deleted, not flagged) | `models/chats.py:2824-2832` |

**Until G1 decides, a request is handled like this** (it depends on the user):

1. The registrar (`<owner>`) records the request and identifies the user and the conversation (date, title).
2. The user opens the conversation in Open WebUI, chooses *Download > Export chat (.json)* (or exports all chats
   under *Data Controls* in the settings), and hands the file to the registrar through the records channel.
3. If the user cannot or will not export it, or has deleted it, there is no admin route with the pilot settings:
   escalate to the records owner and the data protection officer (open points below).

**The three candidate routes, until G1 picks one** (weakness review §4 "Export route", chat design §13.3, register G1
with W2, to-verify #173). None of them is the registrar's procedure until G1 has decided; then this section keeps the
chosen one and drops the others.

1. *The user's own export.* Works with the pilot settings and is the interim handling above. It depends on the user:
   nothing can be exported if the user cannot or will not, or has deleted the conversation.
2. *A temporary, logged admin-access window.* `ENABLE_ADMIN_CHAT_ACCESS=true` on the Open WebUI service, recreate it,
   an admin signs in and uses *Admin > Users > Chats*, then the flag goes back and the container is recreated again.
   While it is on, **every** Open WebUI admin can read **every** chat. In the pilot stack the only admin is the
   feedback service account (production-setup.md, *Open WebUI*): nobody signs in as it, and its key reaches only the
   feedback routes. So the window needs a person with the admin role for its duration, which W2 prefers to avoid, and
   how the window is logged is not settled. Not tested.
3. *Another route G1 names.* The chat design lists a read-only query as the third option, but direct SQL against Open
   WebUI's database is ruled out (weakness review §4, which overrides the chat design). With the pilot settings Open
   WebUI's API has no admin route to another user's conversation (table above), so this route exists only once G1
   defines one.

Also for G1 and W2:

- *Feedback snapshots* cover only rated conversations, as of the rating.
- *Backups of Open WebUI's data directory*: none in P0, so a deleted conversation cannot be recovered.
- The Open WebUI stack is off by default and not deployed in P0 (production-setup.md, *Open WebUI*).

## Gateway keys, budgets and the read-back

Each service calls the gateway with its own **credential key** (production-setup.md, *Gateway credential keys*). From
the gateway's integration phase 2, a call for an assistant or knowledge base that has a gateway key of its own is made
**as that key**: the service sends its credential key and the object's key id (`x-ume-on-behalf-of`), and the gateway
charges the call to that key's budget and limits (identity design §4.7). So two different keys can fail:

- **The object's key** belongs to a department. The gateway refuses it with `403 delegation_not_allowed`,
  `key_disabled`, `key_revoked` or `key_expired`, or with `402 budget_exceeded` when its own, its team's or its
  department's budget is used up. The department fixes it in the gateway; nothing is wrong with the deployment.
- **The service's credential key**: every gateway `401` is that key's own, whichever object the call was for. That is
  for the operators and the gateway admin.

Rag-admin's mirror of the gateway keys (`config."GatewayKeys"`) is refreshed every 5 minutes by the organisation sync,
or at once with *Fetch now* on the *Organisation* page; chat and the admin UI judge a key by it.

| Symptom | Meaning | Fix |
|---|---|---|
| Security event 5011 `rag.gateway.delegation_missing` ("Gateway call … aborted: no echo of the gateway key …"); chat answers `502 upstream_error`; worker documents or evaluations fail with `gateway.delegation_not_applied`; `acted.outcome` of `GET /api/gateway/status` is `echo_missing` | A gateway instance answered a call made as an object's key without echoing the key id. That is an **older gateway instance during the gateway's rolling update**: it ignores the header and would have charged the credential key, past the department's budget. Ume.Rag discards the answer before use (fails closed) because `Gateway:RequireDelegationEcho` is `true` | Nothing to change here: the events stop once every gateway instance runs the new release. If they go on, ask the gateway team (an instance left on an old version, or a proxy that drops the `x-ume-on-behalf-of` response header). **Keep `Gateway:RequireDelegationEcho` at `true`**; `false` lets such answers through, charged to the platform's owner team. Run the failed documents or evaluations again afterwards (a new run of the source, or the evaluation) |
| Chat users get "The assistant is unavailable: its gateway key is disabled." (`503 assistant_unavailable`; also *has expired*, *has been revoked*, *the team or department of its gateway key is inactive*, *is not known to the platform*, *the gateway does not let this platform use its key*, *cannot be used now*); the test panel answers the same as `test_assistant_unavailable`; the assistant's page shows *Unavailable* with the reason | The assistant's gateway key is disabled, revoked or expired, its owner is inactive, it is no longer assigned to the integration *Ume.Rag* (`delegation_not_allowed`, or `missing` once the sync has dropped it), or it shares no residency with the service's credential key (*Unavailable*: "allows no residency in common"). Chat refuses from the mirror before any model call, and the gateway's own `403` gives the same answer | The department fixes it in the gateway: its key's managers re-enable the key, or a department admin chooses another active key of the department on the assistant and publishes. A revoked or expired key cannot come back. Then wait for the next sync or use *Fetch now* (above) |
| Chat users get `402 budget_exceeded` with the gateway's message (it names the key's, the team's or the department's budget); the assistant's page shows *Unavailable*: "The key's budget is used up" (or the team's, the department's; only with the read-back, below) | A budget in the gateway is used up | The department raises the budget in the gateway, or waits for its next period. Budgets are never set in Ume.Rag |
| Worker jobs wait with `gateway.budget_exhausted` or `gateway.key_unavailable` (`content."Jobs"`: `State` `queued`, `LastError` the reason, `AvailableAt` the next full hour); the knowledge base's page shows *Unavailable* | The knowledge base's key is refused: `gateway.budget_exhausted` for a `402` (a budget used up), `gateway.key_unavailable` for a `403 delegation_not_allowed`, `key_disabled`, `key_revoked` or `key_expired`. Each try counts as an attempt; after the job's last attempt the document fails with the same reason | As the two rows above: the department fixes the key or the budget in the gateway; the jobs continue at the next full hour. For documents that failed meanwhile, start a new run of the source. To see what waits: `dc exec -T postgres psql -U postgres -d ragdb -c "SELECT \"Class\", \"LastError\", count(*), min(\"AvailableAt\") FROM content.\"Jobs\" WHERE \"State\" = 'queued' AND \"LastError\" LIKE 'gateway.%' GROUP BY 1, 2"` |
| A gateway `401` in any service's log, for any call, the read-back included | The service's own credential key failed: wrong, expired, revoked, the rotation's grace period over, or the request came from an address outside the key's address list. Never the object's key, and never shown on an assistant or knowledge base | *Secrets and certificates* below and production-setup.md, *Gateway credential keys*: ask the gateway admin, re-run `init-deployment.sh` with `--gateway-credential-key-file SERVICE=FILE`, restart that service |
| rag-admin warns "Test panel usage of assistant … not written (… calls): no admin-console client row of kind internal" | The registry has no `internal` client managed by the deployment (`admin-console`), so test-panel cost is not recorded (the panel still answers) | rag-migrate creates it on every run (production-setup.md, *The admin test panel's client*): check that `rag-migrate` completed (`dc logs rag-migrate`: "The admin test panel's client admin-console: …"), and run it again. Turns made before it are not recovered |

**The read-back's reasons.** When the gateway names its Integration API (`integration_api_url`), rag-admin reads the gateway's cost and
budgets per key from its Integration API (production-setup.md, *Cost and budget read-back*). *Models* shows it in the *AI gateway* card; the status behind it (`GET /api/organisation/sync`, and
`GET /api/gateway/status`) carries `integration`: `configured`, and `lastError` with `reason`, `status` and
`at` when the last read gave nothing; rag-admin also logs "The gateway's Integration API refused or failed (HTTP …,
reason); no read-back this time". The *Cost* cards then show Ume.Rag's figures only, and a used-up budget shows no
badge (the gateway still refuses the calls).

| `configured` / `reason` | Meaning | Fix |
|---|---|---|
| `configured: false` | The gateway's context names no Integration API (`integration_api_url` null: the gateway's `UME_INTEGRATION_API_URL` is unset, or the gateway is older than v0.10.2), or rag-admin has not read its context yet | The gateway admin sets `UME_INTEGRATION_API_URL` as production-setup.md says, if the read-back is wanted; rag-admin picks it up within 5 minutes |
| `scope_missing` (403) | rag-admin's credential key lacks the scope `usage.read` | The gateway admin adds `usage.read` to that key; the next read picks it up (only answers are cached), no restart needed |
| `rate_limited` (429) | More than the gateway's 60 reads a minute for the key (its `Admin:IntegrationRequestsPerMinute`) | Passes by itself; answers are cached 5 minutes per key. If it persists, ask the gateway team about the limit |
| `unauthorized` (401) | rag-admin's credential key itself failed, as in the `401` row above; also when the Admin API sees the request from an address outside the key's address list | As the `401` row: check the key with the gateway admin, and that its address list holds the address the Admin API sees |
| `not_found` (404) | The key is not one of the integration *Ume.Rag*'s keys | It was moved out of the integration in the gateway; the next organisation sync drops it from the mirror |
| `unavailable` (no status) | No answer: wrong URL, DNS, the host firewall (`llm` reaches the address only with `--allow llm=HOST:PORT` in a drop-in of `rag-firewall.service`, *Host firewall*), a network firewall, a certificate rag-admin does not trust, or a timeout | Check the gateway's `UME_INTEGRATION_API_URL`, the firewall's `--allow` and `sudo systemctl start rag-firewall.service`, and `--ca-file` for the Admin API's CA |
| `failed` | Any other answer (a `5xx`, a `400`) | rag-admin's log has the status; ask the gateway team |

## Sign-in and the setup wizard

rag-admin's sign-in provider, client and secret, and Open WebUI's, are set in the setup wizard (identity design §19;
first setup: [production-setup.md](production-setup.md), *3. Sign-in*, or [portainer.md](portainer.md), *First deploy*
step 5). After setup they change only in the wizard after a re-open, never from an admin session. A value the
deployment sets (`deploy/.env`, the secret files, the Portainer variables, the Kubernetes literals) wins and is locked
there.

**Re-open:** give `UME_RAG_SETUP_REOPEN` (Kubernetes: `Setup__Reopen`) a new value, for example today's date, and
`dc up -d` (Portainer: update the stack; Kubernetes: `deploy.sh`). rag-admin opens the wizard once, for one hour, and
prints a new setup code: `dc logs rag-admin | grep 'Setup code'` (Kubernetes: `kubectl logs deploy/rag-admin`, either
pod). The current sign-in keeps working until a platform admin's test sign-in passes and they finish. A restart doesn't
open it again; the next re-open needs another new value.

- **Rotating a client secret:** create the new secret at the provider (keep the old one valid), re-open, type the new
  secret and its expiry date, test and finish, then remove the old one at the provider. Home reminds platform admins from 30 days
  before the expiry date typed there. A declared secret is rotated where it is declared instead:
  `init-deployment.sh --oidc-client-secret-file` (Kubernetes: `create-secrets.sh` with the same option), or `UME_RAG_OIDC_CLIENT_SECRET`
  and update the stack (Portainer); rag-admin follows the file at the next sign-in.
- **Locked out** (sign-in broken: a revoked secret, a moved provider, a certificate rag-admin no longer trusts): re-open
  and fix the values in the wizard, which tests them before saving. Or declare the working values (`--oidc-authority`
  and `--oidc-client-secret-file` with a re-run of `init-deployment.sh`, then `docker compose --env-file deploy/.env
  up -d rag-admin` and `sudo systemctl start rag-firewall.service`, since a restart alone does not read a changed
  `deploy/.env`; Portainer: the variables and an update of the stack): a deployment value always wins. *Sign-in is not set up* on the sign-in page means no usable authority, or a
  stored secret rag-admin can't read.
- **Restoring a database:** empty the re-open value first (*Restore*, above), or the restored database may open the
  wizard again for an hour. The secrets the wizard stored are encrypted with Data Protection: they can be read only
  with the same `data-protection.pfx`. With another certificate sign-in fails until you re-open and type them again.
- **Setup never finished and the hour ran out:** `dc restart rag-admin` opens it again with a new code.

**Security events** ([security-events.md](security-events.md) has the fields and the alerts):

| Event | Means |
|---|---|
| 6008 `rag.admin.setup.opened` | Setup opened at rag-admin's start: `first_run` or `reopen`. Expect one per re-open you asked for |
| 6009 `rag.admin.setup.code_refused` | Wrong setup codes (sampled, with a count); bursts mean someone is guessing |
| 6010 `rag.admin.setup.session` | Someone entered the right code and got a setup session |
| 6011 `rag.admin.setup.test` | A test finished: rag-admin's test sign-in or Open WebUI's client check, passed or the step that failed |
| 6012 `rag.admin.setup.completed` | Sign-in values saved and setup closed; the audit entry names the tested person |
| 6013 `rag.admin.setup.openwebui_saved` | Open WebUI's sign-in values saved in setup mode (setup stays open) |

## Incidents

- **Security events** go to the SIEM under `Ume.Rag.Security`; what each one means and which alerts to set up is in
  [security-events.md](security-events.md).
- **Who to tell:** the service owner `<owner>` and on-call `<owner>` first; the data protection officer `<owner>` for
  any suspected personal-data breach (the GDPR breach assessment: a notifiable breach is reported within 72 hours of
  discovery); the municipality's IT security `<owner>` per its reporting routine; the gateway team `<owner>` when the
  gateway or sign-in is involved (the pilot signs in through the Keycloak shared with the gateway, on the gateway's
  host); the records owner `<owner>` before anything that may be an allmän handling is deleted.
- **Preserve evidence**: logs, the audit log and the security events. Do not capture conversations to investigate.
- **Safe shutdown**: `dc stop` (keeps the volumes). For a bad data change: a point-in-time restore to just before it
  (*Restore*).

### Where to look first

| Symptom | Where to look and what to do |
|---|---|
| Postgres log: archive command failed, pgBackRest error `[103]`; `rag_backup_wal_archive_failed_total` rising | The stanza does not exist yet: `dc exec -T postgres pgbackrest --stanza=ragdb stanza-create` (once per new database or repository) |
| `rag_backup_repo_status` 99 | pgBackRest cannot read the repository: is the share mounted (`UME_RAG_BACKUP_DIR`), are the S3 endpoint and credentials right, does the firewall's `backup` rule have the endpoint? `dc exec -T postgres pgbackrest --stanza=ragdb info` shows the error |
| `rag_backup_wal_archive_age_seconds` above 300 | WAL is not being archived: `dc logs --since 30m postgres`, and `SELECT * FROM pg_stat_archiver` through `dc exec -T postgres psql -U postgres -d ragdb` |
| `rag-backup.service` failed | `journalctl -u rag-backup.service`; a failed run leaves the previous backups intact. Run it again by hand once the cause is fixed |
| Upload copy: "the upload copy's directory … does not exist (is the backup share mounted?)" | Mount the share; the next run catches up |
| `rag-clamav` unhealthy after a signature update | On a 16 GB host a failed reload leaves clamd without an engine (it still answers PING; every scan fails). The restart timer restarts it within a few minutes; meanwhile nothing can be scanned (the worker's scan step, P1, fails closed). `journalctl -u rag-restart-unhealthy.service`, `dc logs rag-clamav`. On a 24 GB host clamd keeps its old engine during a reload |
| Admin API answers 503 `config_busy` | Another config change held the config version lock for more than 5 s. Retry; if it persists, look for a long transaction holding the lock on `config."ConfigVersion"` in `pg_stat_activity` |
| `rag-migrate` exited non-zero; api, admin and worker do not start | `dc logs rag-migrate`. They wait for a successful migration, so the old schema is untouched; fix the cause and `dc up -d --wait` again |
| A service exits at once with a permission error on `/run/app-secrets` or `/run/certs` | The files belong to the wrong user: run the `chown` commands `init-deployment.sh` printed (or re-run it as root) |
| One service's model calls fail: admin code `gateway_key_missing`, or the gateway answers `401` | No credential key in `secrets/<service>/Gateway__Key` (`init-deployment.sh` lists the services without one), or the key is wrong, expired or revoked: ask the gateway admin, then re-run `init-deployment.sh` with `--gateway-credential-key-file SERVICE=FILE` and restart that service (production-setup.md, *Gateway credential keys*) |
| `rag-firewall.service` failed: "cannot resolve HOST" | DNS for an allow-listed name failed; the previous rules stay in place. Check the name in `deploy/.env` |
| `rag-firewall.service` failed: "no DOCKER-USER chain" | Docker is not running, or runs without its iptables integration |
| `install.sh` warns "Docker does not use the systemd cgroup driver" | The ingestion slice caps nothing: set `native.cgroupdriver=systemd` (production-setup.md), restart Docker, then `dc up -d --force-recreate` |
| `escrow-keys.sh --verify`: "differs from the deployment's key" | A key was rotated after that escrow: escrow again and verify |
| `rag-renderer` restarts in a loop; its log says "No usable sandbox" | Chromium cannot create the user namespaces of its sandbox: the seccomp profile is not applied (`images/renderer/seccomp.json` missing next to `deploy/`), or the host restricts unprivileged user namespaces (AppArmor on Ubuntu 23.10+). Don't turn the sandbox off: production-setup.md, *Renderer* |
| `rag-renderer` exits at once: "UME_RAG_RENDER_WS_PATH is required" | `secrets/renderer.env` is missing: re-run `init-deployment.sh` (it keeps every other secret) |
| Uploads and fetched files wait with `clamav.unavailable`; `dc ps` lists no `rag-clamav` | The worker's malware scan is `on` (`UME_RAG_CLAMAV_MODE` in `deploy/.env`, or no such line) but `COMPOSE_PROFILES` lacks `clamav`, as in a deployment updated without re-running `init-deployment.sh` (upgrade guide): re-run it with your options (it writes the profile; add `--clamav off` only to store files unscanned), then `dc up -d --wait` |
| Worker log warns at every start that the malware scan is off; security event 5106 `rag.malware.scan_off`; files show *Not scanned for malware* | The deployment runs with `--clamav off` (`UME_RAG_CLAMAV_MODE=off`): intended there, or re-run `init-deployment.sh` with your options plus `--clamav on`, then `dc up -d --wait`. Files stored meanwhile stay unscanned (production-setup.md, *Malware scan*) |
| Sources with fetch mode `browser` wait with `render.not_configured`; `dc ps` lists no `rag-renderer` | The renderer is off (its Compose profile `render`, production-setup.md, *Renderer*): re-run `init-deployment.sh` with your options plus `--render`, then `dc up -d --wait` and `dc restart rag-worker` |
| Every render fails with `render.unavailable` and `dc ps` lists no `rag-renderer` | The worker has `secrets/worker/Render__Endpoint` but `COMPOSE_PROFILES` in `deploy/.env` lacks `render`: re-run `init-deployment.sh` with or without `--render`, so the two match, then `dc up -d --wait` and `dc restart rag-worker` |
| The parser stays unhealthy for minutes after a start | It warms its slots and runs a self-test first; the worker reports it in its own health and retries |
| Worker health unhealthy on `embedding-canary`; embed, preview and evaluation jobs wait with `embed.canary_mismatch` | The last embedding canary check of a profile came out below 0.999: the gateway's model alias now answers with another model, which would mix vector spaces. Ask the gateway team what changed; once the profile's model is restored, run **Check embedding canary** again (admin UI, *Models*). The jobs continue when a check passes. A new model is a profile migration, not a fix here |

## Secrets and certificates

- **Internal certificates** last 825 days. `rag-cert-expiry.timer` (deploy/host/README.md) fails and names the
  certificate from 60 days before it expires (`systemctl --failed`, `journalctl -u rag-cert-expiry`, or
  `rag_cert_expiry_timestamp_seconds` in `certs.prom`). Bring `ca.key` back to `deploy/certs/`, run
  `sudo ./deploy/init-deployment.sh --renew-certs`, restart `postgres` and `rag-parser` (they read their certificates
  only at start; the script's closing note says so), and move `ca.key` off the host again. `--renew-ca` replaces the
  internal CA and re-issues its certificates: then restart the whole stack. Portainer:
  [portainer.md](portainer.md#internal-certificates).
- **The Data Protection certificate and the pepper** are never regenerated: a new one makes the stored data
  unreadable (protected values, every key hash).
- **The blob master key and the pgBackRest passphrase** are generated once and escrowed. There is no rotation
  procedure yet (key provider decision K1).
- **The api's keys** (`Security__DownloadGrantKeys__<kid>`, `Security__ConversationIdKeys__<kid>`): a rotation adds a
  new key id next to `k1`.
- **The gateway credential keys** (`secrets/{api,admin,worker}/Gateway__Key`, one per service of the gateway integration
  *Ume.Rag*): a gateway admin issues and rotates them; rotate one with the gateway's 24-hour grace period, re-run
  `init-deployment.sh` with `--gateway-credential-key-file SERVICE=FILE`, and restart that service
  ([production-setup.md](production-setup.md), *Gateway credential keys*, which also lists each key's settings). Gateway
  `401` answers in one service's log mean its key expired, was revoked or the rotation's grace period ended first.
- **The OIDC client secrets** of rag-admin and Open WebUI: rotated through a re-open of the setup wizard, or where they
  are declared (*Sign-in and the setup wizard*). `secrets/admin/Setup__Secret` only derives setup codes: it needs no
  backup or rotation.
- **The feedback service account's Open WebUI key** (`secrets/openwebui-feedback/OpenWebUi__AdminApiKey`; Portainer:
  `rag-secrets/openwebui-feedback/`): delete the file and run Open WebUI's `up` again (Portainer: update the stack);
  `open-webui-setup` mints a new one, the old one stops working at once, and rag-api reads the new one without a restart.
  Its password stays in `secrets/openwebui.env` and is never used: password sign-in is off.

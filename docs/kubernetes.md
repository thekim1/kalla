# Kubernetes

`deploy/kubernetes` has Kustomize manifests for running Källa on Kubernetes, with updates that do not interrupt
clients, in the shape of the gateway's (its [Kubernetes guide](https://github.com/thekim1/fors/blob/main/docs/kubernetes.md)).
They are the same services, settings, certificates and secrets as the [Docker Compose setup](production-setup.md), and
the certificates and secrets come from the same script as theirs (`deploy/portainer/bootstrap.sh`, which
`create-secrets.sh` runs on your machine). Docker Compose stays the default. The design is
[`repo-and-deployment.md`](../plans/platform/repo-and-deployment.md) §10.

## What you get

| Part | Contents |
|---|---|
| `base/` | kalla-api and kalla-admin (2 replicas each, rolling updates, PodDisruptionBudgets, probes on `/health/*`), their Services, the `kalla-migrate` Job, the blob volume `rag-blobs` and the NetworkPolicies: default deny, then only the stack's own flows (below) |
| `components/datastores/` | Optional single-instance Postgres (StatefulSet with the `kalla/postgres` image: pgvector, pgBackRest and WAL archiving, as in Compose), TLS and separate database roles |
| `components/ingestion/` | kalla-worker (1 replica, stop-then-start) and kalla-parser: crawling, uploads and parsing |
| `components/clamav/` | kalla-clamav, the malware scan, with its signature volume and NetworkPolicies; in the example by default (below) |
| `components/renderer/` | Optional kalla-renderer for JavaScript-rendered pages (needs a seccomp profile on the nodes, below) |
| `components/ingress-controller/` | The NetworkPolicy that lets the Ingress controller reach kalla-api and kalla-admin; in the example by default (below) |
| `example/` | An overlay to copy: namespace, images, settings, Ingress, the blob volume's storage class |
| `create-secrets.sh` | Generates the certificates and secrets and creates the Kubernetes secrets from them |
| `deploy.sh` | Deploys or updates an overlay: Postgres, the migration, then a rolling update |

Every pod runs as non-root with a read-only file system, no privileges and the runtime's default seccomp profile (the
renderer: its own Localhost profile), so the namespace can enforce the **restricted** Pod Security Standard.
Requirements: Kubernetes 1.32 or later (the role-group settings, such as `Oidc__RoleGroups__rag-platform-admin`, are
environment variables with a `-` in their name, allowed by default from 1.32), `kubectl` 1.27 or later (it includes
Kustomize), an Ingress controller or Gateway API implementation, and a **ReadWriteMany** storage class for the blob
volume (NFS, CephFS, Azure Files, Longhorn RWX).

## Install

1. **Generate the certificates and secrets and create the Kubernetes secrets**, in one step, on a machine with
   `openssl` and `kubectl`:

   ```bash
   ./deploy/kubernetes/create-secrets.sh --namespace kalla --domain example.se \
       --gateway-credential-key-file api=kalla-api.key --gateway-credential-key-file admin=kalla-admin.key \
       --gateway-credential-key-file worker=kalla-worker.key \
       --datastores --backup-target s3://s3.example.se/kalla --backup-s3-credentials-file s3.cred [--renderer]
   ```

   `--domain` gives the host names `rag.<domain>` and `rag-admin.<domain>` (`--api-host` and `--admin-host` name
   others). Leave out `--datastores` and the backup options if you bring your own Postgres (below), and `--renderer`
   without the renderer component. `--ca-file FILE` adds a CA the services must trust (a gateway or sign-in provider
   with a certificate from your own CA). Sign-in is set in the setup wizard after the first deploy (*Sign-in*, below);
   to declare it instead, add `--oidc-client-secret-file kalla-admin.secret` (and `Oidc__Authority` in the overlay).
   `--help` lists every option.

   The script runs `deploy/portainer/bootstrap.sh`, the bootstrap of the Compose and Portainer stacks, into
   `deploy/certs` and `deploy/secrets`: an internal CA, a certificate per service, database passwords, the key pepper,
   the blob master key, the Data Protection certificate, kalla-admin's setup secret, the pgBackRest repository settings
   and connection strings to the host `postgres` (the Service in `components/datastores`). Each service's gateway
   credential key (issued by a gateway admin: [production-setup.md](production-setup.md), *Gateway credential keys*)
   goes to `deploy/secrets/<service>/Gateway__Key` and into `kalla-<service>-secrets`; the key's address list then
   holds the cluster's egress addresses. **Escrow the keys** outside the cluster (runbook, *Key escrow*): without them
   a restored database is unusable. `deploy/escrow-keys.sh --secrets-dir deploy/secrets --certs-dir deploy/certs
   /media/<escrow>` writes the set, `--verify` reads it back, and `--restore` puts it into new directories before the
   first run of a restored installation. Keep the two directories: the next run reads them.

   **Running it again** keeps every generated secret and every value an option gave before (a credential key, the
   client secret, the backup repository); the host names, `--ca-file` and your own certificates are given on every
   run. It re-issues a service certificate that expires within 30 days, which needs `deploy/certs/ca.key` (keep it
   offline in between). After a run that changed something, restart the deployments (`kubectl rollout restart`; for a
   rotated credential key, passed with `--gateway-credential-key-file SERVICE=FILE`, only `deployment/rag-<service>`).

   **An installation made with `init-deployment.sh`** (before this script generated the files itself): run
   `create-secrets.sh` with the same `--certs-dir` and `--secrets-dir`. It keeps the CA, the passwords, the keys, the
   pepper and the Data Protection certificate, and issues the five service certificates again from the same CA, so
   have `ca.key` in place and restart the deployments afterwards.

3. **Make an overlay.** Copy `deploy/kubernetes/example` (for example to `deploy/kubernetes/prod`, or into your GitOps
   repository with the `../base` paths adjusted) and change:
   - `images`: the release's images (`ghcr.io/thekim1/kalla/<service>`), pinned by digest from the release notes.
     A release bundle already pins the base and components. The packages are public; a cluster without Internet
     access mirrors them.
   - `Gateway__BaseUrl`, `Knowledge__PublicBaseUrl` (and the same address as kalla-admin's `Admin__ApiPublicUrl`) and,
     only for an identity provider without roles in its tokens, the break-glass
     `Oidc__RoleGroups__rag-platform-admin` (a `rag-` group name or an Entra object id); the other roles' groups are set
     in RAG's admin (*Settings → Role groups*). The other `Oidc__*` literals stay commented out unless you declare
     sign-in (*Sign-in*, below).
   - The blob volume's `storageClassName` (ReadWriteMany). With ReadWriteOnce, every pod that mounts it (kalla-api,
     kalla-admin, kalla-worker) must run on one node.
   - The Ingress host names and TLS secrets. The pods speak HTTPS, so the controller must connect with TLS: the example
     annotations are for ingress-nginx; with Traefik use a ServersTransport, with the Gateway API an HTTPRoute and a
     BackendTLSPolicy. Pass the Host header through, turn off response buffering for kalla-api (streamed answers) and
     request buffering for the admin host (uploads up to 60 MB).
   - The NetworkPolicy patches, for the destinations the base's policies do not open: an Ingress controller outside
     the namespace `ingress-nginx`, `Crawl__AllowedPrivateRanges`, the gateway at a private address or on another port
     than 443 (*Network policies*, below).
   - Optionally `../components/renderer`, more replicas, resources, node placement; or no malware scan (*Malware
     scan*, below).

4. **Deploy:**

   ```bash
   ./deploy/kubernetes/deploy.sh deploy/kubernetes/prod
   ```

5. **Once per new database**, create the pgBackRest stanza (WAL archiving fails until it exists):

   ```bash
   kubectl -n kalla exec statefulset/postgres -- pgbackrest --stanza=ragdb stanza-create
   ```

6. **Set up sign-in** (*Sign-in*, below): register `https://<admin host>/signin-oidc` and `/signout-callback-oidc`
   on the `kalla-admin` client at your identity provider, finish the setup wizard (or sign in, when sign-in is
   declared), and list the Ingress controller's pod network under the trusted proxies (admin UI, Settings, *Network*),
   so kalla-api reads the client address from `X-Forwarded-For`.

Settings go into the overlay as ConfigMap literals named the way the apps read them (`Section__Key`), for example
`OTEL_EXPORTER_OTLP_ENDPOINT` or `Crawl__AllowedPrivateRanges`. Secrets stay in the secret files.

## Sign-in

kalla-admin's sign-in is set in its setup wizard (identity design §19). The base sets no `Oidc__*` value.

1. After the first `deploy.sh`, read the setup code: `kubectl -n kalla logs deploy/kalla-admin | grep 'Setup code'`.
   Both pods print the same code; `deploy.sh` reminds you while setup is open. The code is valid for one hour; while
   setup was never finished, `kubectl -n kalla rollout restart deployment/kalla-admin` prints a new one. Treat it like a
   password: whoever holds it chooses the sign-in provider while setup is open.
2. Open `https://<admin host>/setup` through the Ingress (the redirect URIs are built from the address you open it at)
   and enter the code.
3. The wizard shows the redirect URIs, takes the provider address, the client ID (default `kalla-admin`), the client
   secret and its optional expiry date, and the claim names under *Advanced* (presets for Keycloak and Entra ID).
   **Test sign-in** opens the provider in a new window; sign in as yourself. *Finish* is offered only when the test
   passed for a platform admin (the role `rag-platform-admin`, or a group in `Oidc__RoleGroups__rag-platform-admin`).

The base's NetworkPolicies let kalla-admin reach any address on 443, so the wizard's test reaches the provider; one on
another port needs its own egress rule (*Network policies*).

**Declaring sign-in instead:** each `Oidc__*` literal the overlay sets (`Oidc__Authority`, `Oidc__ClientId`,
`Oidc__TenantId`, `Oidc__RefuseGuests`, the claim names) is the deployment's value and is locked in the wizard; the
example overlay shows them commented out. With `Oidc__Authority` and the client secret (`create-secrets.sh
--oidc-client-secret-file`, which puts `Oidc__ClientSecret` in `kalla-admin-secrets`) the wizard never opens.

**Opening the setup wizard again** (a client secret about to expire, a new provider, a broken sign-in): set
`Setup__Reopen` in the overlay's `kalla-admin-config` to a new value (for example today's date) and deploy. kalla-admin
opens the wizard once and prints a new setup code, valid for one hour, in `kubectl -n kalla logs deploy/kalla-admin`
(either pod); the current sign-in keeps working until you finish. Before restoring a database, remove the literal
first. The code's key is `Setup__Secret` in `kalla-admin-secrets`, generated once by `create-secrets.sh`; it needs
no backup. The client secrets the wizard stores need the same `data-protection.pfx` (runbook, *Sign-in and the setup
wizard*). Kubernetes has no Open WebUI.

## Updates without downtime

Change the image digests in the overlay and run `deploy.sh` again. It:

1. applies configuration, volumes and Postgres, and waits for Postgres. When the Postgres image changed (a restart of
   the database: a short outage of every service, announce it), it runs `ALTER EXTENSION vector UPDATE`;
2. deletes the previous `kalla-migrate` Job (a Job cannot change), runs it with the migrator's credentials and waits
   until it succeeds. If it fails, it stops here, with the old version still running;
3. applies the rest. kalla-api and kalla-admin roll one pod at a time (`maxUnavailable: 0`, `maxSurge: 1`). A new pod
   receives traffic only once `/health/ready` passes. An old pod then fails readiness, leaves the Service endpoints
   during its 10-second drain while still answering (with `Connection: close`), and finishes its requests within the
   shutdown timeout (kalla-api 310 s). The worker is replaced stop-then-start (`Recreate`), as in Compose. How this works
   is described in [Updates without downtime](zero-downtime-updates.md).

Only releases the [upgrade guide](upgrade-guide.md) marks **Rolling update: yes** may be rolled this way, and until the
first production deployment releases may break compatibility. For the others, scale kalla-api and kalla-admin to 0, run
`deploy.sh`, and they come back with the new version.

**Argo CD or Flux** apply the overlay themselves. Run the migration first by marking the Job as a pre-sync hook in your
overlay, for example for Argo CD:

```yaml
patches:
  - target: {kind: Job, name: kalla-migrate}
    patch: |-
      - op: add
        path: /metadata/annotations
        value: {argocd.argoproj.io/hook: PreSync, argocd.argoproj.io/hook-delete-policy: BeforeHookCreation}
```

## Your own Postgres

`components/datastores` runs one Postgres, so updating it is a short outage and its standby is yours to add. In
production prefer your platform's database service (Postgres 17 with pgvector 0.8 or later) or an operator
(CloudNativePG). Then:

- Create the database and roles as `deploy/postgres/init.sql` does: `kalla_migrator` owns `ragdb`; `kalla_api`,
  `kalla_admin` and `kalla_worker` may only connect, with the connection limits there. The `kalla-migrate` Job
  grants the rest and creates the `vector` extension if the migrator may.
- Require TLS. The connection strings verify the server certificate against `ca.pem` (`SSL Mode=VerifyFull`): pass
  your database's CA to `create-secrets.sh` with `--ca-file`, which adds it for api, admin, worker and migrations.
- Run `create-secrets.sh` with `--postgres-host <your database's host>` and without `--datastores`, and remove the
  component from the overlay. The connection strings are written on every run from that host name and the passwords
  in `deploy/secrets/postgres/<role>_password` (`migrator`, `api`, `admin`, `worker`): give the roles those
  passwords, or put your own in the files and run the script again.
- Backups (and the point-in-time restore of the runbook) are then your platform's.

## Malware scan

`components/clamav` runs `kalla-clamav`, which the worker uses to scan every upload and fetched file (fail-closed); the
example overlay includes it. The worker's mode is the ConfigMap literal `ClamAv__Mode` in `kalla-worker-config`:
the base sets `on`, so a worker without `kalla-clamav` fails closed and its files wait with `clamav.unavailable`. To run
**without a malware scan**, do both in your overlay: remove `- ../components/clamav`, and uncomment
`- ClamAv__Mode=off` under `kalla-worker-config` (the overlay's value replaces the base's). The worker then stores
files unscanned and says so: a warning and security event 5106 at every start, and in the admin UI the Files card's
summary counts the files not scanned for malware ([production setup](production-setup.md), *Malware scan*, which also says what turning it on later does not do).
The mode is not derived from the component on purpose: forgetting one of the two steps stops ingestion instead of
silently storing unscanned files.

Removing the component from an overlay that ran with it does not delete what `kubectl apply` created before:
`kubectl -n kalla delete deployment,service,pvc -l app.kubernetes.io/component=kalla-clamav` removes `kalla-clamav` and
its signature volume. To turn the scan on again, add the component back, remove the `ClamAv__Mode=off` line, and run
`deploy.sh`; freshclam downloads the signatures first (several minutes), and files stored meanwhile stay unscanned.

## Renderer

Chromium's sandbox needs user namespaces, which the runtime's default seccomp profile blocks, so `kalla-renderer` uses the
repository's profile `images/renderer/seccomp.json` (Docker's default plus the namespace calls) as a **Localhost**
profile. Copy it to every node that may run the pod, before enabling the component:

```bash
sudo install -D -m 0644 images/renderer/seccomp.json /var/lib/kubelet/seccomp/kalla/renderer.json
```

(or with a DaemonSet or the Security Profiles Operator). On nodes where AppArmor restricts unprivileged user
namespaces (Ubuntu 23.10 and later), check that the pod becomes ready. The worker reaches it at
`ws://kalla-renderer:3000/<secret path>` (its `Render__Endpoint`, from `create-secrets.sh --renderer`).

## Network policies

The base denies all traffic to and from the stack's pods (`kalla-default-deny`), then allows only what the Compose
networks and host firewall allow (`base/networkpolicies.yaml`). kalla-parser has no authentication, clamd neither
authentication nor TLS, and Postgres accepts the app roles from any address, so these policies are what keeps other
pods in the cluster away from them. On by default:

- DNS (port 53) from every pod.
- kalla-api, kalla-admin, kalla-worker and `kalla-migrate` to Postgres (5432), and Postgres from them only
  (`kalla-database`, `kalla-postgres`). Postgres has no egress.
- The worker to the parser (8000), clamd (3310, with `components/clamav`) and the renderer (3000); those three accept
  the worker only. The parser and the renderer get no egress at all.
- kalla-api and kalla-admin out on 443 to any address (gateway, IdP, Open WebUI).
- The worker (the crawl, the gateway) and ClamAV (freshclam) out on 80 and 443 to **public addresses only**: the rule is
  `0.0.0.0/0` except `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `169.254.0.0/16` (the cloud metadata service),
  `100.64.0.0/10` and `127.0.0.0/8`. A gap in the crawler's guard therefore cannot reach the API server, the metadata
  service or other pods and Services.
- With `components/ingress-controller` (in the example), the Ingress controller to kalla-api and kalla-admin on 8443. The
  base admits nothing inbound to them, so without that component, or a policy of your own for another entry, the
  stack is unreachable.

The flows are listed in [Network and ports](network-and-ports.md). NetworkPolicy cannot filter by host name: keep the
public destinations in your egress firewall or proxy, and give every destination the rules above do not cover its own
rule in the overlay. The example overlay has a commented patch for each:

- **Ingress controller.** The policy `kalla-ingress-controller` admits the namespace `ingress-nginx`; replace it with
  yours (the label `kubernetes.io/metadata.name`). Another in-cluster client of kalla-api's Service, such as Open WebUI
  calling it directly rather than through the Ingress, needs a rule of its own.
- **Private crawl ranges.** Each range in `Crawl__AllowedPrivateRanges` needs the same range as an egress rule of
  `kalla-worker` (`ipBlock`, ports 80 and 443): the crawler's guard and the policy must both allow it.
- **The gateway** at a private address, or on a port other than 443 (its Compose stack publishes 8443): an egress rule
  of `kalla-worker` for its address and port; add the port to `kalla-api` and `kalla-admin` too, and the Admin
  API's (9443) to `kalla-admin` with the read-back. The same for an IdP not on 443 and the OTLP collector (4317).
- **pgBackRest's S3 store**, with `components/datastores`: an egress rule of `kalla-postgres`. Until then WAL
  archiving and the stanza fail. freshclam's proxy, if it uses one: `kalla-clamav`.
- **Your own Postgres:** replace the destination of `kalla-database` with an `ipBlock` for your database.

The policies need a CNI that enforces them (Calico, Cilium, Antrea and others); with one that does not, they are
silently ignored. They cover IPv4 only: on a dual-stack cluster the pods get no IPv6 egress. Kubelet probes come from
the node and are not blocked by them with the common CNIs. The manifests render in CI, but no test in this repository
runs the stack with them enforced: check the stack after deploying (sign in to the admin UI, ask a question, crawl a
page, upload a file).

## Scaling and placement

- **kalla-api and kalla-admin** are stateless (all state in Postgres) and spread across nodes where possible; the
  PodDisruptionBudgets keep one of each through node drains. Two replicas each fit the database roles' connection
  limits (api 26 = 2 × 12, admin 12 = 2 × 5). More replicas need a higher `CONNECTION LIMIT` (upgrade guide,
  *Superuser steps*) and `max_connections`, or PgBouncer in transaction mode in front of Postgres (design §10). Each
  replica applies the request rate limits on its own.
- **kalla-worker** stays at one replica: a second would exceed the worker role's connection limit.
- **Ingestion on its own nodes:** the parser and the worker are CPU-heavy (OCR). Give `components/ingestion` a
  `nodeSelector` and toleration in your overlay so they never take CPU from kalla-api (design §10).

## Tested

The manifests render in CI (Kustomize 5.7.1, the example overlay with every component, and without
`components/clamav`). No cluster run is
recorded yet; record the first in `plans/work/results-a.md`.

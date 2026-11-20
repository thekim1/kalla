// Everything the website says that is not a Markdown page. The documentation pages themselves are the Markdown files
// listed in DOCS; the build renders them, so the repository and the website never disagree. The build and the styles
// are the gateway's site (github.com/thekim1/llmgateway, site/), adapted.

export const GH = 'https://github.com/thekim1/ume-rag-platform'
export const GATEWAY_SITE = 'https://thekim1.github.io/llmgateway/'

/**
 * Documentation sidebar, grouped by what the reader came to do: try or install it, run it, check it, or look something
 * up. `source` is relative to the repository root; `slug` is the page's URL under /docs/.
 */
export const DOCS = [
  { group: 'Get started', items: [
    { slug: '', title: 'Introduction', source: 'docs/introduction.md', lead: 'What Ume.Rag does, how it sits on the LLM gateway, and where to go next.' },
    { slug: 'how-it-works', title: 'How it fits together', source: 'docs/how-it-works.md', lead: 'Knowledge bases, assistants, apps and keys, gateway keys and sign-in, and how a request is checked.' },
    { slug: 'compose-quick-start', title: 'Quick start with Compose', source: 'docs/compose-quick-start.md', lead: 'From an empty Linux host to a first knowledge base: the gateway and Ume.Rag on one machine.' },
    { slug: 'local-development', title: 'Local development', source: 'docs/local-development.md', lead: 'Run the full stack with Aspire, demo data, the fake gateway and the test users, and run the tests.' },
  ] },
  { group: 'Deploy', items: [
    { slug: 'production-setup', title: 'Production setup', source: 'docs/production-setup.md', lead: 'The pilot host with Docker Compose: services, sizing, certificates, backups and the reverse proxy.' },
    { slug: 'portainer', title: 'Portainer', source: 'docs/portainer.md', lead: 'Ume.Rag and the gateway as one Portainer stack: a first deploy and the upgrade in one update.' },
    { slug: 'kubernetes', title: 'Kubernetes', source: 'docs/kubernetes.md', lead: 'Kustomize manifests with the same services, settings and secrets as the Compose setup.' },
    { slug: 'keycloak', title: 'Keycloak', source: 'docs/keycloak.md', lead: 'The admin UI\'s OIDC client and roles in Keycloak.' },
    { slug: 'network-and-ports', title: 'Network and ports', source: 'docs/network-and-ports.md', lead: 'Every listener, outbound destination and internal port per setup, for the firewall and proxy teams.' },
  ] },
  { group: 'Run it', items: [
    { slug: 'runbook', title: 'Runbook', source: 'docs/runbook.md', lead: 'Day-to-day operation: backups and restore, certificates, keys, crawls and incidents.' },
    { slug: 'upgrade-guide', title: 'Upgrade guide', source: 'docs/upgrade-guide.md', lead: 'What to do per release, rolling updates, rollback and the release checklist.' },
    { slug: 'zero-downtime-updates', title: 'Updates without downtime', source: 'docs/zero-downtime-updates.md', lead: 'Two instances behind a load balancer, replaced one at a time.' },
    { slug: 'test-host', title: 'Test host', source: 'docs/test-host.md', lead: 'A release run the way the pilot will, before go-live.' },
  ] },
  { group: 'Security and compliance', items: [
    { slug: 'security-events', title: 'Security events', source: 'docs/security-events.md', lead: 'The events the services log for a SIEM, with stable ids and what to do about each.' },
    { slug: 'accessibility-statement', title: 'Accessibility statement', source: 'docs/accessibility-statement.md', lead: 'A draft for the pilot: automated evidence and outstanding manual checks.' },
  ] },
  { group: 'Reference', items: [
    { slug: 'changelog', title: 'Changelog', source: 'CHANGELOG.md', lead: 'Changes per release.' },
  ] },
]

/** Folders copied as they are, so Markdown can link to them. */
export const DOC_ASSETS = []

export const SDK = [
  { id: 'python', label: 'Python', code: `import os
from openai import OpenAI

client = OpenAI(
    base_url="https://rag.example.se/v1",
    api_key=os.environ["UME_RAG_KEY"],   # an app key from the admin UI
)

reply = client.chat.completions.create(
    model="bygglov",                     # an assistant, by its slug
    messages=[{"role": "user", "content": "Behöver jag bygglov för en altan?"}],
    extra_body={"ume": {"related_documents": 5, "follow_ups": 3}},
)
print(reply.choices[0].message.content)  # cited: … [1]` },
  { id: 'node', label: 'Node.js', code: `import OpenAI from "openai";

const client = new OpenAI({
  baseURL: "https://rag.example.se/v1",
  apiKey: process.env.UME_RAG_KEY,
});

const stream = await client.chat.completions.create({
  model: "bygglov",
  stream: true,
  messages: [{ role: "user", content: "Behöver jag bygglov för en altan?" }],
});
for await (const chunk of stream) {
  process.stdout.write(chunk.choices[0]?.delta?.content ?? "");
  if (chunk.ume) console.log(chunk.ume);  // outcome, sources, follow-ups
}` },
  { id: 'curl', label: 'curl', code: `curl https://rag.example.se/v1/retrieve \\
  -H "Authorization: Bearer $UME_RAG_KEY" \\
  -H "Content-Type: application/json" \\
  -d '{"query":"bygglov altan","collections":["bygg-och-miljo"]}'

# Hybrid search without an answer: chunks, scores, the
# heading path and each document's modified and checked dates` },
]

export const ENDPOINTS = ['/v1/chat/completions', '/v1/models', '/v1/feedback', '/v1/retrieve', '/v1/collections', '/v1/documents/{id}']

export const PROOF = [
  { value: 'Every', label: 'answer cited', sub: 'Numbered sources, each with its date and when it was last checked' },
  { value: '1', label: 'way to a model', sub: 'Every model call goes through the LLM gateway, under a residency ceiling' },
  { value: '14 days', label: 'to roll back', sub: 'A published knowledge base can go back to its previous version' },
  { value: 'Nightly', label: 'evaluation', sub: 'Test questions run every night, with an alert when recall@10 drops' },
]

export const PILLARS = [
  { icon: 'format_quote', tone: 'ok', title: 'Answers you can check', body: 'Every claim points to a numbered source, with the page\'s date and when it was last checked. When the sources have no answer, the assistant says so and names who to contact.', points: ['Citations and related documents', '"Not found" instead of a guess', 'A hand-off to support with a short summary'] },
  { icon: 'travel_explore', tone: 'accent', title: 'Knowledge that keeps up', body: 'Crawls follow robots.txt and sitemaps, check pages for changes and removals, and render JavaScript pages when needed. Uploaded files are virus-scanned before they are read.', points: ['Web pages, PDF, Office, images and scans (OCR)', 'Personal data found and flagged', 'Preview, evaluate, then publish'] },
  { icon: 'shield_lock', tone: 'warn', title: 'Built for a municipality', body: 'Runs on your own servers, behind your own login. Each department owns its knowledge bases and assistants, and pays for its model use through its own gateway key.', points: ['Residency ceilings: on-prem, EU', 'Sign-in with Keycloak, Entra ID or AD FS', 'Audit log and security events for your SIEM'] },
]

/** Screenshots from a local stack with demo content (site/public/screenshots, each in light, dark and Lumen). */
export const UI_SHOTS = [
  { file: 'knowledge-base', alt: 'A knowledge base\'s overview: its contact, the assistants that use it, documents, last fetch, quality and cost', title: 'Knowledge bases', body: 'Sources, fetches, test questions and quality, then publish, with what it costs.' },
  { file: 'assistant', alt: 'An assistant\'s draft: its instructions in Swedish and the knowledge base it searches', title: 'Assistants', body: 'Instructions, knowledge bases and answer settings, with a test panel and versions.' },
  { file: 'map', alt: 'The map: each department\'s knowledge bases, the assistants that use them, the channel and who is responsible', title: 'Map', body: 'Who owns what: from a knowledge base to the assistant and the channel that offers it.' },
]

export const DEPLOYS = [
  { icon: 'deployed_code', title: 'Docker Compose', body: 'One service file and one bootstrap. The gateway and Ume.Rag on one host, from two downloads and two setup wizards.', meta: 'deploy/compose.rag.yaml', href: 'docs/compose-quick-start/' },
  { icon: 'web', title: 'Portainer', body: 'Ume.Rag and the gateway as one stack, every image pinned by digest. A new stack needs one typed value.', meta: 'compose.portainer.yaml', href: 'docs/portainer/' },
  { icon: 'hub', title: 'Kubernetes', body: 'Kustomize manifests with the same services, settings and secrets, and the same bootstrap.', meta: 'deploy/kubernetes', href: 'docs/kubernetes/' },
]

export const PATHS = [
  { id: 'local', title: 'Try it locally', body: 'Docker and Aspire. Demo data, a fake gateway and test users.', icon: 'laptop_mac',
    prereqs: ['.NET SDK 10.0', 'Aspire 13.6', 'Docker 28+', 'Node 22+', 'uv'],
    steps: [
      { title: 'Clone the repository', body: 'Install the admin UI packages once, and enable the pre-commit hook.', shell: 'bash', code: `git clone ${GH}.git
cd ume-rag-platform
(cd src/admin-ui && npm ci)
git config core.hooksPath .githooks` },
      { title: 'Start the stack', body: 'Aspire starts Postgres, Keycloak, the fake gateway, the parser, ClamAV, the renderer, the services, the admin UI and Open WebUI. Demo data is on by default.', shell: 'PowerShell', code: `.\\scripts\\Start-Dev.ps1          # with demo data
.\\scripts\\Start-Dev.ps1 -Empty   # empty database, only the test users`, note: 'Or run aspire run (or dotnet run --project src/Kalla.AppHost). The Aspire dashboard is at https://localhost:17470.' },
      { title: 'Sign in to the admin UI', body: 'Open https://localhost:5174 and sign in as one of the Keycloak test users. Their password is the dev-user-password parameter in the AppHost\'s user secrets; the dashboard shows it.' },
      { title: 'Ask a question', body: 'Open WebUI is at http://localhost:7410, with the demo assistants as models. The fake gateway answers instantly, so you see the whole path: search, citations and the answer.' },
    ] },
  { id: 'production', title: 'Deploy a release', body: 'A Linux Docker host, the LLM gateway and your own login provider.', icon: 'dns',
    prereqs: ['Linux + Docker 28+', 'Compose 2.33.1+', '8 cores, 20 GB RAM (with the gateway)', 'An OIDC provider', 'The LLM gateway'],
    steps: [
      { title: 'Set up the gateway', body: 'Ume.Rag calls every model through the Ume AI Gateway. Install its release first and finish its setup wizard.', shell: 'bash', code: `curl -fsSLO https://github.com/thekim1/llmgateway/releases/download/vX.Y.Z/ume-llm-gateway-X.Y.Z.tar.gz
tar -xzf ume-llm-gateway-X.Y.Z.tar.gz && cd ume-llm-gateway-X.Y.Z
sudo ./deploy/init-deployment.sh --gateway-host <host> --admin-host <host> --bind 0.0.0.0 --yes
sudo docker compose -f deploy/compose.prod.yaml --env-file deploy/.env up -d --wait` },
      { title: 'Download Ume.Rag and let the gateway create its keys', body: 'The release ships a manifest that the gateway applies: an integration with the three credential keys rag-api, rag-admin and rag-worker call it with.', shell: 'bash', code: `curl -fsSLO ${GH}/releases/download/vX.Y.Z/ume-rag-X.Y.Z.tar.gz
tar -xzf ume-rag-X.Y.Z.tar.gz
sudo install -m 644 ume-rag-X.Y.Z/deploy/portainer/integration-manifest.json \\
  ume-llm-gateway-X.Y.Z/deploy/integration-manifests/ume-rag.json`, note: 'Ume.Rag\'s images on ghcr.io are private: sudo docker login ghcr.io with a token that holds read:packages.' },
      { title: 'Start Ume.Rag', body: 'The script writes deploy/.env; the first start creates the certificates and secrets and lists what is still missing.', shell: 'bash', code: `cd ume-rag-X.Y.Z
sudo ./deploy/init-deployment.sh --api-host <host> --admin-host <host> \\
  --gateway-url https://<host>:8443 --gateway-ca-file ../ume-llm-gateway-X.Y.Z/deploy/certs/ca.pem \\
  --gateway-secrets-dir ../ume-llm-gateway-X.Y.Z/deploy/integration-secrets --backup-target local
sudo docker compose --env-file deploy/.env up -d --wait` },
      { title: 'Finish the setup wizard, then add content', body: 'Read the setup code from rag-admin\'s log, open https://<host>:9543/setup and set sign-in with a test sign-in. Then follow Get started on the admin UI\'s Home: a knowledge base, a source, the first fetch, test questions, publish, an assistant.' },
    ] },
]

export const GLOSSARY = [
  ['Knowledge base', 'A collection of sources (websites, URL lists, uploaded files) that is fetched, processed, evaluated and published together.'],
  ['Source', 'Where a knowledge base\'s documents come from: a crawl of a site, a list of URLs or uploads.'],
  ['Assistant', 'What a user chats with: instructions, the knowledge bases it may search and its answer settings. Clients ask for it by its slug as the model.'],
  ['Publish', 'Makes a knowledge base\'s or an assistant\'s new version the one that answers; the previous version can come back within 14 days.'],
  ['Citation', 'A numbered reference in the answer to the chunk it came from, with the document\'s date and when it was last checked.'],
  ['Residency ceiling', 'The furthest a model call may go: on-prem or EU. The gateway refuses any route above it.'],
  ['Embedding profile', 'The embedding model and vector size a knowledge base is indexed with.'],
]

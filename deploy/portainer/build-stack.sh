#!/bin/sh
# Generates deploy/portainer/compose.portainer.yaml: the gateway's Portainer stack (its deploy/compose.portainer.yaml,
# unchanged but for one line, below) plus Ume.Rag's services from deploy/compose.rag.yaml, as one stack to paste into
# Portainer.
#
#   deploy/portainer/build-stack.sh                # the compose.portainer.yaml asset of the gateway's latest release
#   deploy/portainer/build-stack.sh v0.10.1        # ... of that gateway release
#   deploy/portainer/build-stack.sh current        # ... of the gateway release the committed file was built from
#   deploy/portainer/build-stack.sh ../llmgateway/deploy/compose.portainer.yaml   # or a local file
#
# Options, before the gateway:
#   --check          write nothing: build from `current` and fail with the difference when the committed
#                    compose.portainer.yaml is not what deploy/compose.rag.yaml gives (verify.yml's deploy-checks)
#   --images FILE    first pin the RAG_*_IMAGE defaults of deploy/compose.rag.yaml to FILE's lines (a release's
#                    images.env: RAG_API_IMAGE=registry/repo:tag@sha256:..., one per image), then build; release.yml's
#                    pull request after a release
#
# A gateway release's asset pins the gateway's images by digest; its repository file does not (they are then required
# variables), so build from a release unless testing an unreleased gateway change. A named release is downloaded with
# curl (the gateway's repository is public); only the latest one is looked up with gh.
#
# deploy/compose.rag.yaml is the one Ume.Rag service file, which Compose runs directly. Its marks are applied first: a
# line "#@joined <line>" becomes <line>, and a line that ends in "#@alone" is left out (what a stack without the gateway
# needs of its own: the network llm, the host directories, the x-hardened anchor, which the gateway's file has). Then
# its x- anchors go after the gateway's, and its services, configs, networks and volumes at the end of the gateway's
# same sections; each "file: path" config (relative to deploy/) becomes inline content, indented, with $ escaped as $$,
# as the gateway's build-portainer-stack.sh does. stack-header.txt, the comment Portainer operators read, goes before
# the gateway's name:. Run again after a gateway or Ume.Rag change.
#
# Two lines of the gateway's part are rewritten. Its adminapi's
#   Gateway__IntegrationApiUrl: ${UME_INTEGRATION_API_URL:-}
# gets https://adminapi:8443/integration/v1 as its default (unset only), the address rag-admin reaches it at in this
# stack, so the cost and budget read-back needs no variable. And its bootstrap's
#   UME_INTEGRATION_MANIFEST: ${UME_INTEGRATION_MANIFEST:-}
# gets integration-manifest.json (beside this script, written on one line) as its default, so the gateway creates the
# integration Ume.Rag and its three credential keys with nothing typed (M-20 S8). The default applies only when the
# variable is unset ("-", not ":-"): a stack that sets its own manifest keeps it, and an empty value creates nothing.
# The manifest names no owner, which the gateway accepts from 0.11.0. The build stops when the
# gateway's file does not have exactly that line.
# A default that contains "}" needs Compose 2.24.7 or later (compose-go 5ede864, "default values can contain curly
# braces"); an older one ends the expression at the first "}" without an error, also when the variable is set (tried
# with Compose 2.17.3 to 5.7.1, 2026-11-16). Portainer has a later one from 2.21 (its pinned versions, not run), and
# the stack's volume subpaths are newer than that Compose too (not tried on an older one).
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
rag="$here/../compose.rag.yaml"
header="$here/stack-header.txt"
manifest="$here/integration-manifest.json"
committed="$here/compose.portainer.yaml"
check=0; images=""
while [ $# -gt 0 ]; do
  case "$1" in
    --check) check=1; shift ;;
    --images) images="${2:?--images needs a file}"; shift 2 ;;
    -*) echo "unknown option: $1" >&2; exit 1 ;;
    *) break ;;
  esac
done
gateway="${1:-}"
[ "$check" = 0 ] || gateway=current
tmp="$(mktemp)"; out_tmp="$(mktemp)"; trap 'rm -f "$tmp" "$out_tmp"' EXIT

if [ -n "$images" ]; then
  [ "$check" = 0 ] || { echo "--check writes nothing: it cannot be combined with --images" >&2; exit 1; }
  # A reference pinned by digest; the character set also keeps it safe in the sed replacement below.
  while IFS='=' read -r var ref; do
    [ -n "$var" ] || continue
    printf '%s' "$ref" | grep -Eq '^[a-z0-9][a-z0-9._/:-]*@sha256:[0-9a-f]{64}$' || { echo "$var in $images is not pinned by digest: $ref" >&2; exit 1; }
    grep -q "\${${var}:-" "$rag" || { echo "deploy/compose.rag.yaml has no \${${var}:-...}" >&2; exit 1; }
    sed -i -E "s#\\\$\\{${var}:-[^}]*\\}#\${${var}:-${ref}}#g" "$rag"
  done < "$images"
  echo "Pinned the images of deploy/compose.rag.yaml to $images"
fi

if [ -z "$gateway" ] || [ ! -f "$gateway" ]; then
  case "$gateway" in
    "") tag="$(gh release view -R thekim1/llmgateway --json tagName --jq .tagName)" ;;
    current)
      tag="$(grep -m1 -oE 'llmgateway/gateway:[0-9][0-9A-Za-z.-]*' "$committed" | cut -d: -f2)"
      [ -n "$tag" ] || { echo "$committed names no gateway release (llmgateway/gateway:X.Y.Z)" >&2; exit 1; }
      tag="v$tag" ;;
    *) tag="$gateway" ;;
  esac
  curl -fsSL -o "$tmp" "https://github.com/thekim1/fors/releases/download/$tag/compose.portainer.yaml" \
    || { echo "could not download compose.portainer.yaml of the gateway's release $tag" >&2; exit 1; }
  echo "The gateway's stack from its release $tag"
  gateway="$tmp"
fi
out="$committed"; [ "$check" = 0 ] || out="$out_tmp"
python3 - "$gateway" "$rag" "$header" "$here/.." "$out" "$manifest" <<'EOF'
import json, re, sys
gateway_path, rag_path, header_path, deploy_dir, out_path, manifest_path = sys.argv[1:]
read = lambda path: open(path, encoding="utf-8").read().replace("\r\n", "\n")
gw = read(gateway_path).split("\n")

# The integration manifest as the default of the gateway's UME_INTEGRATION_MANIFEST (the header says why). On one line,
# in a single-quoted YAML scalar; "$" would start a variable for Compose and "'" would end the scalar.
manifest = json.dumps(json.load(open(manifest_path, encoding="utf-8")), separators=(",", ":"), ensure_ascii=True)
if "$" in manifest or "'" in manifest:
    sys.exit(f"{manifest_path} must not contain $ or '")
passed = "UME_INTEGRATION_MANIFEST: ${UME_INTEGRATION_MANIFEST:-}"
at = [i for i, l in enumerate(gw) if l.strip() == passed]
if len(at) != 1:
    sys.exit(f"the gateway's file has {len(at)} lines '{passed}', expected 1: see this script's header")
indent = gw[at[0]][: len(gw[at[0]]) - len(gw[at[0]].lstrip())]
gw[at[0]:at[0] + 1] = [
    indent + "# Rewritten by the RAG repository's build-stack.sh: the integration Ume.Rag and its three credential keys as the",
    indent + "# default (deploy/portainer/integration-manifest.json), used only while the variable is unset. Set, the variable",
    indent + "# wins; set and empty, nothing is provisioned.",
    indent + "UME_INTEGRATION_MANIFEST: '${UME_INTEGRATION_MANIFEST-" + manifest + "}'",
]

# The Integration API's address as the default of the gateway's UME_INTEGRATION_API_URL: in this stack rag-admin
# shares the edge network with adminapi, so the address is always the same and nobody has to type it.
passed = "Gateway__IntegrationApiUrl: ${UME_INTEGRATION_API_URL:-}"
at = [i for i, l in enumerate(gw) if l.strip() == passed]
if len(at) != 1:
    sys.exit(f"the gateway's file has {len(at)} lines '{passed}', expected 1: see this script's header")
indent = gw[at[0]][: len(gw[at[0]]) - len(gw[at[0]].lstrip())]
gw[at[0]:at[0] + 1] = [
    indent + "# Rewritten by the RAG repository's build-stack.sh: adminapi's own address in this stack as the default, used",
    indent + "# only while the variable is unset. Set and empty, the gateway announces no Integration API.",
    indent + "Gateway__IntegrationApiUrl: ${UME_INTEGRATION_API_URL-https://adminapi:8443/integration/v1}",
]

# Ume.Rag's file as the joined stack has it: the marks applied, then its top-level blocks (the file's own comment, before
# the first key, is dropped).
joined = []
for line in read(rag_path).split("\n"):
    if re.search(r"(^|\s)#@alone$", line.rstrip()):
        continue
    m = re.match(r"^( *)#@joined (.*)$", line)
    joined.append(m.group(1) + m.group(2) if m else line)
blocks = []  # [key, lines after the key's line]
for line in joined:
    m = re.match(r"^([A-Za-z_][\w-]*):", line)
    if m:
        blocks.append([m.group(1), [line]])
    elif blocks:
        blocks[-1][1].append(line)
sections = {"x": []}
for key, lines in blocks:
    if key.startswith("x-"):
        sections["x"] += lines
    elif key in ("services", "configs", "networks", "volumes"):
        sections[key] = lines[1:]
    else:
        sys.exit(f"{rag_path}: the top-level {key}: has no place in the joined stack (mark it #@alone)")
# Each "file: path" config as inline content.
configs = []
for line in sections.get("configs", []):
    m = re.match(r"^( *)file: (\S+)$", line)
    if not m:
        configs.append(line)
        continue
    indent, path = m.groups()
    configs.append(indent + "content: |")
    for inc in read(f"{deploy_dir}/{path.removeprefix('./')}").rstrip("\n").split("\n"):
        inc = inc.replace("$", "$$")
        configs.append(indent + "  " + inc if inc else "")
sections["configs"] = configs
sections["header"] = read(header_path).split("\n")
for body in sections.values():
    while body and body[-1] == "":
        body.pop()

top = [i for i, l in enumerate(gw) if re.match(r"^[A-Za-z_][\w-]*:", l)]
def block_end(key):
    starts = [i for i in top if gw[i].startswith(key + ":")]
    if not starts:
        sys.exit(f"the gateway's file has no top-level {key}:")
    later = [i for i in top if i > starts[0]]
    end = later[0] if later else len(gw)
    while end > starts[0] + 1 and gw[end - 1].strip() == "":
        end -= 1
    return end

inserts = []  # (line index, lines)
name_line = next(i for i in top if gw[i].startswith("name:"))
inserts.append((name_line, sections.pop("header") + [""]))
first_service = next(i for i in top if gw[i].startswith("services:"))
inserts.append((first_service, sections.pop("x") + [""]))
for key, body in sections.items():
    inserts.append((block_end(key), ([""] if key == "services" else []) + body))

inserts.append((0, ["# GENERATED FILE: deploy/portainer/build-stack.sh of the RAG repository (thekim1/ume-rag-platform) writes it",
                    "# from the gateway's deploy/compose.portainer.yaml (below, unchanged but for the default of",
                    "# UME_INTEGRATION_MANIFEST and of UME_INTEGRATION_API_URL)",
                    "# and deploy/compose.rag.yaml, with deploy/portainer/stack-header.txt.",
                    "# Do not edit it by hand; the gateway's own note below refers to the gateway's part.", "#"]))
out = list(gw)
for index, lines in sorted(inserts, key=lambda p: p[0], reverse=True):
    out[index:index] = lines
open(out_path, "w", encoding="utf-8", newline="\n").write("\n".join(out).rstrip("\n") + "\n")
EOF
if [ "$check" = 1 ]; then
  if ! diff -u "$committed" "$out" > "$tmp.diff"; then
    head -60 "$tmp.diff"; rm -f "$tmp.diff"
    echo "deploy/portainer/compose.portainer.yaml is not what deploy/compose.rag.yaml gives: run deploy/portainer/build-stack.sh current" >&2
    exit 1
  fi
  rm -f "$tmp.diff"
  echo "deploy/portainer/compose.portainer.yaml is current"
else
  echo "Wrote deploy/portainer/compose.portainer.yaml"
fi

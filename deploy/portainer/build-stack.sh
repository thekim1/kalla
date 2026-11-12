#!/bin/sh
# Generates deploy/portainer/compose.portainer.yaml: the gateway's Portainer stack (its deploy/compose.portainer.yaml,
# unchanged) plus RAG's services from compose.rag.template.yaml, as one stack to paste into Portainer.
#
#   deploy/portainer/build-stack.sh                         # the gateway's file from github.com/thekim1/llmgateway main
#   deploy/portainer/build-stack.sh ../llmgateway/deploy/compose.portainer.yaml   # or a local copy
#
# Each "@SECTION name" block of the template goes into the same top-level section of the gateway's file (header: after
# its comment, x: after its x- anchors), and "@INCLUDE path" lines (relative to deploy/) are inlined, indented, with $
# escaped as $$, as the gateway's build-portainer-stack.sh does. Run again after a gateway or RAG change.
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
gateway="${1:-}"
tmp="$(mktemp)"; trap 'rm -f "$tmp"' EXIT
if [ -z "$gateway" ]; then
  gh api repos/thekim1/llmgateway/contents/deploy/compose.portainer.yaml --jq .content | base64 -d > "$tmp"
  gateway="$tmp"
fi
python3 - "$gateway" "$here/compose.rag.template.yaml" "$here/.." "$here/compose.portainer.yaml" <<'EOF'
import re, sys
gateway_path, template_path, deploy_dir, out_path = sys.argv[1:]
gw = open(gateway_path, encoding="utf-8").read().replace("\r\n", "\n").split("\n")

sections, current = {}, None
for line in open(template_path, encoding="utf-8").read().replace("\r\n", "\n").split("\n"):
    if line.startswith("@SECTION "):
        current = line.split()[1]; sections[current] = []; continue
    if current is None:
        continue  # the template's own comment
    m = re.match(r"^( *)@INCLUDE (.+)$", line)
    if m:
        indent, path = m.groups()
        for inc in open(f"{deploy_dir}/{path}", encoding="utf-8").read().replace("\r\n", "\n").rstrip("\n").split("\n"):
            inc = inc.replace("$", "$$")
            sections[current].append(indent + inc if inc else "")
        continue
    sections[current].append(line)
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
                    "# from the gateway's deploy/compose.portainer.yaml (below, unchanged)",
                    "# and deploy/portainer/compose.rag.template.yaml.",
                    "# Do not edit it by hand; the gateway's own note below refers to the gateway's part.", "#"]))
out = list(gw)
for index, lines in sorted(inserts, key=lambda p: p[0], reverse=True):
    out[index:index] = lines
open(out_path, "w", encoding="utf-8", newline="\n").write("\n".join(out).rstrip("\n") + "\n")
EOF
echo "Wrote deploy/portainer/compose.portainer.yaml"

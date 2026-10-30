"""Download the models of ume_parser/model_manifest.json into the image (parser design §12).

Every file is fetched at the pinned commit of its Hugging Face repository and checked against its SHA-256 before it
is moved into place; a mismatch fails the build. This is the only image build step that downloads anything outside
the locked Python wheels and the dated Debian snapshot. Standard library only, so it runs before the venv exists.

    python scripts/download_models.py --out /opt/models
"""

import argparse
import hashlib
import json
import sys
import tempfile
import urllib.request
from pathlib import Path

MANIFEST = Path(__file__).resolve().parent.parent / "ume_parser" / "model_manifest.json"
CHUNK = 1024 * 1024


def download(url: str, target: Path, sha256: str) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    digest = hashlib.sha256()
    with tempfile.NamedTemporaryFile(dir=target.parent, delete=False) as tmp:
        tmp_path = Path(tmp.name)
        try:
            with urllib.request.urlopen(url, timeout=60) as response:  # noqa: S310 (https URL built from the manifest)
                while chunk := response.read(CHUNK):
                    tmp.write(chunk)
                    digest.update(chunk)
        except BaseException:
            tmp_path.unlink(missing_ok=True)
            raise
    if digest.hexdigest() != sha256:
        tmp_path.unlink(missing_ok=True)
        raise SystemExit(f"SHA-256 mismatch for {url}: expected {sha256}, got {digest.hexdigest()}")
    tmp_path.chmod(0o644)
    tmp_path.replace(target)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--out", type=Path, required=True, help="models directory (the image uses /opt/models)")
    parser.add_argument("--manifest", type=Path, default=MANIFEST)
    args = parser.parse_args()

    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    for model in manifest["models"]:
        for path, sha256 in sorted(model["files"].items()):
            url = f"https://huggingface.co/{model['repository']}/resolve/{model['revision']}/{path}"
            print(f"{model['name']}: {path}", flush=True)
            download(url, args.out / model["dir"] / path, sha256)
    return 0


if __name__ == "__main__":
    sys.exit(main())

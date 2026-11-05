"""The image ships its third-party notices (licence review P2, stream A item A-1).

Runs in the image (`docker build --target test`), skipped elsewhere: LICENSES.md is at its documented path and equals
what scripts/licences.py writes for the installed packages, the Debian set and the model manifest, and every model
directory carries its licence text.
"""

import json
import os
import subprocess
from pathlib import Path

import pytest

from scripts import licences
from ume_parser.settings import Settings

LICENSES = Path("/usr/share/doc/ume-rag-parser/LICENSES.md")

# The image's working directory; a checkout on a runner or a laptop is never there.
pytestmark = pytest.mark.skipif(
    Path(__file__).resolve().parent.parent != Path("/app"), reason="needs the built image (run in the image)"
)


def test_licences_file_is_in_the_image():
    assert LICENSES.is_file()


def test_licences_file_is_current(tmp_path):
    runtime = tmp_path / "runtime.txt"
    env = {**os.environ, "UV_PYTHON_DOWNLOADS": "never", "UV_OFFLINE": "1"}
    command = ["uv", "export", "--no-cache", "--frozen", "--no-dev", "--no-emit-project", "--no-hashes", "-q"]
    subprocess.run(  # noqa: S603 (fixed arguments)
        [*command, "-o", str(runtime)],
        check=True,
        env=env,
        cwd=Path(licences.__file__).resolve().parent.parent,
    )
    expected = licences.render(
        licences.runtime_names(runtime), licences.MANIFEST, licences.DPKG_STATUS, licences.snapshot_from_sources()
    )
    assert LICENSES.read_text(encoding="utf-8") == expected, "LICENSES.md is stale: rerun scripts/licences.py"


def test_every_model_has_its_licence():
    models = json.loads(licences.MANIFEST.read_text(encoding="utf-8"))["models"]
    for model in models:
        assert (Settings().models_dir / model["dir"] / "LICENSE").is_file(), model["name"]

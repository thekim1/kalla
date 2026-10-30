"""`GET /v1/info` (parser design §13.1): versions, models, limits, formats, capacity and `build_hash`.

Everything except `capacity` (`models.StaticInfo`) is collected once at start-up, without importing Docling, and
hashed into `build_hash`: the SHA-256 of its canonical JSON (sorted keys, no whitespace, UTF-8). The worker records
the hash on every run item and keys the parse cache by it, so any change of code revision, library, model or limit
changes it.
"""

import hashlib
import importlib.metadata
import json
import platform
import shutil
import subprocess
import tomllib
from pathlib import Path
from typing import Any

from fastapi import APIRouter, Request

from ume_parser import limits
from ume_parser.models import Build, Capacity, Info, StaticInfo
from ume_parser.settings import Settings

PACKAGE_DIR = Path(__file__).resolve().parent
PYPROJECT = PACKAGE_DIR.parent / "pyproject.toml"
MODEL_MANIFEST = PACKAGE_DIR / "model_manifest.json"

PACKAGES = (
    "docling-slim",
    "docling-core",
    "docling-parse",
    "docling-ibm-models",
    "torch",
    "torchvision",
    "transformers",
    "pypdfium2",
)
SUPPORTED_FORMATS = ("pdf",)
DEFERRED_FORMATS = ("doc", "xls", "ppt", "rtf", "eml", "msg")

router = APIRouter()


def _parser_version() -> str:
    with PYPROJECT.open("rb") as f:
        return tomllib.load(f)["project"]["version"]


def _pdfium() -> dict[str, Any]:
    """The bundled pdfium build (`pypdfium2_raw/version.json`), read without loading the library."""
    path = importlib.metadata.distribution("pypdfium2").locate_file("pypdfium2_raw/version.json")
    raw = json.loads(Path(str(path)).read_text(encoding="utf-8"))
    return {"build": raw["build"], "origin": raw["origin"], "flags": raw["flags"]}


def _tesseract() -> dict[str, Any] | None:
    executable = shutil.which("tesseract")
    if executable is None:
        return None

    def run(*args: str) -> list[str]:
        result = subprocess.run([executable, *args], capture_output=True, text=True, timeout=10, check=True)  # noqa: S603
        return result.stdout.splitlines()

    version = run("--version")[0].removeprefix("tesseract ").strip()
    languages = sorted(line.strip() for line in run("--list-langs")[1:] if line.strip())
    return {"version": version, "languages": languages}


def _models() -> list[dict[str, Any]]:
    manifest = json.loads(MODEL_MANIFEST.read_text(encoding="utf-8"))
    return [
        {k: model[k] for k in ("name", "repository", "revision", "licence", "files")} for model in manifest["models"]
    ]


def collect(settings: Settings) -> StaticInfo:
    """The hashed part of the info document."""
    return StaticInfo.model_validate(
        {
            "parser": {"version": _parser_version(), "revision": settings.revision},
            "python": platform.python_version(),
            "packages": {name: importlib.metadata.version(name) for name in PACKAGES},
            "pdfium": _pdfium(),
            "tesseract": _tesseract(),
            "models": _models(),
            "limits": limits.as_dict(),
            "formats": {"supported": list(SUPPORTED_FORMATS), "deferred": list(DEFERRED_FORMATS)},
        }
    )


def build_hash(static: StaticInfo) -> str:
    canonical = json.dumps(static.model_dump(mode="json"), sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return "sha256:" + hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def build(static: StaticInfo, hash_: str) -> Build:
    """The `build` block of every result and error."""
    return Build(parser=static.parser.version, docling=static.packages["docling-slim"], build_hash=hash_)


@router.get("/v1/info", response_model=Info)
def info(request: Request) -> Info:
    state = request.app.state
    supervisor = state.supervisor
    return Info(
        **state.static_info.model_dump(),
        build_hash=state.build_hash,
        capacity=Capacity(
            convert_slots=supervisor.convert_slots,
            slots_ready=supervisor.slots_ready,
            inspect_slots=0,
            ner_ready=False,
        ),
    )

"""Legacy Word, Excel, PowerPoint and RTF (parser design §3, Z1 brought forward): LibreOffice converts the file to its
OOXML counterpart (`sniff.LEGACY`) in the slot, then the declarative backend reads that like any other upload.

LibreOffice runs headless, as its own process group, with a fresh profile in the job directory (so parallel slots never
share one), macros never run (`--convert-to` with the default macro security and no `macro:` URL), and a timeout below
the job's deadline. Its output and profile are in the job directory, which the API process removes after the request.
Nothing from the document reaches a log: LibreOffice's own output is discarded and only the outcome is logged.
"""

import contextlib
import logging
import os
import shutil
import signal
import subprocess
import time
from pathlib import Path

from ume_parser.errors import ParserError
from ume_parser.models import Format

log = logging.getLogger(__name__)

SOFFICE = os.environ.get("PARSER_SOFFICE", "soffice")
# LibreOffice's import filters for the sniffed formats (`--infilter`): forced, so a file that is not what its sniff
# says fails instead of falling through to LibreOffice's plain-text import.
_IMPORT_FILTERS: dict[Format, str] = {
    "doc": "MS Word 97",
    "xls": "MS Excel 97",
    "ppt": "MS PowerPoint 97",
    "rtf": "Rich Text Format",
}
# LibreOffice's export filters for the targets (`--convert-to <ext>:<filter>`).
_FILTERS: dict[Format, str] = {
    "docx": "MS Word 2007 XML",
    "xlsx": "Calc MS Excel 2007 XML",
    "pptx": "Impress MS PowerPoint 2007 XML",
}
# A cold start of LibreOffice takes a few seconds; the rest of the deadline stays for Docling.
MAX_SECONDS = 120.0
DEADLINE_SHARE = 0.6


def available() -> bool:
    return shutil.which(SOFFICE) is not None


def convert(source: Path, fmt: Format, target: Format, work_dir: Path, deadline_s: float) -> Path:
    """Converts `source` (sniffed as `fmt`) to `target` in `work_dir`; returns the converted file. Raises `ParserError`:
    `conversion_failed` when LibreOffice is missing or writes nothing (a password-protected or broken file), `timeout`
    when it runs too long."""
    if not available():
        raise ParserError("conversion_failed")
    work_dir.mkdir(parents=True, exist_ok=True)
    named = work_dir / f"input.{fmt}"
    # LibreOffice picks its import filter by content, but a matching name keeps it from guessing on short files.
    shutil.copyfile(source, named)
    out_dir = work_dir / "out"
    profile = work_dir / "profile"
    timeout = max(5.0, min(MAX_SECONDS, deadline_s * DEADLINE_SHARE))
    command = [
        SOFFICE,
        "--headless",
        "--invisible",
        "--nologo",
        "--nodefault",
        "--nolockcheck",
        "--norestore",
        "--nofirststartwizard",
        f"-env:UserInstallation={profile.resolve().as_uri()}",
        f"--infilter={_IMPORT_FILTERS[fmt]}",
        "--convert-to",
        f"{target}:{_FILTERS[target]}",
        "--outdir",
        str(out_dir),
        str(named),
    ]
    started = time.monotonic()
    process = subprocess.Popen(  # noqa: S603 (fixed command; the file name is ours)
        command,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        cwd=work_dir,
        env={**os.environ, "HOME": str(work_dir), "SAL_USE_VCLPLUGIN": "svp"},
        start_new_session=True,
    )
    try:
        code = process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        _kill(process)
        log.info("legacy %s: LibreOffice timed out after %.0f s", fmt, timeout)
        raise ParserError("timeout", retry="later") from None
    finally:
        if process.poll() is None:
            _kill(process)
    converted = out_dir / f"input.{target}"
    elapsed_ms = round((time.monotonic() - started) * 1000)
    if code != 0 or not converted.is_file() or converted.stat().st_size == 0:
        log.info("legacy %s: LibreOffice exit %s, no output (%d ms)", fmt, code, elapsed_ms)
        raise ParserError("conversion_failed")
    log.info("legacy %s: converted to %s in %d ms", fmt, target, elapsed_ms)
    return converted


def _kill(process: subprocess.Popen[bytes]) -> None:
    """Kills LibreOffice and every process it started (its own session)."""
    with contextlib.suppress(ProcessLookupError):
        os.killpg(process.pid, signal.SIGKILL)
    process.wait()

"""Legacy Word, Excel, PowerPoint and RTF through LibreOffice and the declarative backends (ume_parser.legacy).

Needs `soffice` (the parser image has it); skipped elsewhere. The legacy inputs are made here by LibreOffice itself from
the repository's OOXML and RTF fixtures, so no binary legacy files are committed.
"""

import shutil
import subprocess

import pytest

from helpers import fixture_bytes
from ume_parser import convert, legacy
from ume_parser.errors import ParserError
from ume_parser.models import ConvertRequest
from ume_parser.sniff import sniff_file

pytestmark = pytest.mark.skipif(not legacy.available(), reason="LibreOffice (soffice) is not installed")

_FILTERS = {"doc": "MS Word 97", "xls": "MS Excel 97", "ppt": "MS PowerPoint 97"}


@pytest.fixture(scope="module")
def pipelines(tmp_path_factory):
    return convert.Pipelines(tmp_path_factory.mktemp("no-models"), 1)


def _make(tmp_path, source: str, fmt: str) -> bytes:
    """`source` (a fixture) saved by LibreOffice as `fmt`."""
    work = tmp_path / f"make-{fmt}"
    work.mkdir()
    (work / source).write_bytes(fixture_bytes(source))
    subprocess.run(  # noqa: S603 (fixed command)
        [
            legacy.SOFFICE,
            "--headless",
            f"-env:UserInstallation={(work / 'profile').as_uri()}",
            "--convert-to",
            f"{fmt}:{_FILTERS[fmt]}",
            "--outdir",
            str(work / "out"),
            str(work / source),
        ],
        check=True,
        capture_output=True,
        timeout=120,
        env={"HOME": str(work), "PATH": "/usr/bin:/bin"},
    )
    return (work / "out" / f"{source.rsplit('.', 1)[0]}.{fmt}").read_bytes()


def _options(request_id: str, ext: str) -> dict:
    request = ConvertRequest.model_validate({"request_id": request_id, "file_ext": ext, "outputs": {"markdown": True}})
    return request.model_dump(mode="json", by_alias=True)


def _run(tmp_path, pipelines, data: bytes, ext: str) -> dict:
    job_dir = tmp_path / "job"
    job_dir.mkdir()
    path = job_dir / "input"
    path.write_bytes(data)
    sniffed = sniff_file(path, data[:1024])
    job = {
        "request_id": "l-1",
        "input": str(path),
        "dir": str(job_dir),
        "format": sniffed.format,
        "options": _options("l-1", ext),
    }
    return convert.run_job(job, pipelines)


@pytest.mark.parametrize(("source", "fmt"), [("text.docx", "doc"), ("sheets.xlsx", "xls"), ("slides.pptx", "ppt")])
def test_legacy_office_is_converted_and_read(tmp_path, pipelines, source, fmt):
    expected = convert.run_job(
        {
            "request_id": "o-1",
            "input": str(_fixture(tmp_path, source)),
            "dir": str(tmp_path),
            "format": None,
            "options": _options("o-1", "." + source.rsplit(".", 1)[1]),
        },
        pipelines,
    )
    out = _run(tmp_path, pipelines, _make(tmp_path, source, fmt), f".{fmt}")
    assert (out["status"], out["pipeline"], out["format"]) == ("success", "docling", fmt)
    assert out["media_type_sniffed"].startswith("application/")
    # The same words as the OOXML original (LibreOffice may change layout details, never the text).
    words = {w for w in expected["markdown"].split() if len(w) > 4 and w.isalpha()}
    got = out["markdown"]
    assert words and sum(w in got for w in words) / len(words) > 0.9


def _fixture(tmp_path, name: str):
    path = tmp_path / name
    path.write_bytes(fixture_bytes(name))
    return path


def test_rtf_is_converted_and_read(tmp_path, pipelines):
    out = _run(tmp_path, pipelines, fixture_bytes("legacy.rtf"), ".rtf")
    assert (out["status"], out["format"], out["media_type_sniffed"]) == ("success", "rtf", "application/rtf")
    assert "Kallelse till sammanträde." in out["markdown"]


def test_a_file_libreoffice_cannot_read_is_conversion_failed(tmp_path):
    broken = tmp_path / "broken.doc"
    broken.write_bytes(bytes.fromhex("d0cf11e0a1b11ae1") + b"\xff" * 4096)
    with pytest.raises(ParserError) as raised:
        legacy.convert(broken, "doc", "docx", tmp_path / "work", 30)
    assert raised.value.code == "conversion_failed"


def test_a_slow_conversion_is_killed_at_its_timeout(tmp_path, monkeypatch):
    sleeper = tmp_path / "soffice"
    sleeper.write_text("#!/bin/sh\nsleep 60\n")
    sleeper.chmod(0o755)
    monkeypatch.setattr(legacy, "SOFFICE", str(sleeper))
    monkeypatch.setattr(legacy, "MAX_SECONDS", 1.0)
    monkeypatch.setattr(shutil, "which", lambda _: str(sleeper))
    source = tmp_path / "in.doc"
    source.write_bytes(b"x")
    with pytest.raises(ParserError) as raised:
        legacy.convert(source, "doc", "docx", tmp_path / "work", 1)
    assert raised.value.code == "timeout"

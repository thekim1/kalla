"""Legacy Word, Excel, PowerPoint and RTF through LibreOffice and the declarative backends (ume_parser.legacy).

The conversions need `soffice` (the parser image has it) and are skipped elsewhere; the tests of the launch and of the
output checks stand in a shell script for it and need only `prlimit`. The legacy inputs are made here by LibreOffice
itself from the repository's OOXML and RTF fixtures, so no binary legacy files are committed.
"""

import shutil
import subprocess

import pytest

from helpers import fixture_bytes
from ume_parser import convert, legacy
from ume_parser.errors import ParserError
from ume_parser.models import ConvertRequest
from ume_parser.sniff import sniff_file

needs_soffice = pytest.mark.skipif(not legacy.available(), reason="LibreOffice (soffice) is not installed")
needs_prlimit = pytest.mark.skipif(shutil.which(legacy.PRLIMIT) is None, reason="prlimit is not installed")

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


@needs_soffice
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


@needs_soffice
def test_rtf_is_converted_and_read(tmp_path, pipelines):
    out = _run(tmp_path, pipelines, fixture_bytes("legacy.rtf"), ".rtf")
    assert (out["status"], out["format"], out["media_type_sniffed"]) == ("success", "rtf", "application/rtf")
    assert "Kallelse till sammanträde." in out["markdown"]


@needs_soffice
def test_a_file_libreoffice_cannot_read_is_conversion_failed(tmp_path):
    broken = tmp_path / "broken.doc"
    broken.write_bytes(bytes.fromhex("d0cf11e0a1b11ae1") + b"\xff" * 4096)
    with pytest.raises(ParserError) as raised:
        legacy.convert(broken, "doc", "docx", tmp_path / "work", 30)
    assert raised.value.code == "conversion_failed"


@needs_prlimit
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


def _fake_soffice(tmp_path, monkeypatch, package: str) -> None:
    """A stand-in for `soffice` that records its process limits in its working directory (the job's work directory)
    and writes the fixture `package` as its output."""
    (tmp_path / "package").write_bytes(fixture_bytes(package))
    script = tmp_path / "soffice"
    # The last two arguments are the output directory and the input file.
    script.write_text(
        "#!/bin/sh\n"
        "cat /proc/$$/limits > limits\n"
        "while [ $# -gt 2 ]; do shift; done\n"
        'mkdir -p "$1"\n'
        f'cp "{tmp_path / "package"}" "$1/input.docx"\n'
    )
    script.chmod(0o755)
    monkeypatch.setattr(legacy, "SOFFICE", str(script))


def _limit(limits: str, name: str) -> int:
    line = next(line for line in limits.splitlines() if line.startswith(name))
    return int(line[len(name) :].split()[0])


@needs_prlimit
def test_libreoffice_runs_under_an_address_space_and_a_file_size_limit(tmp_path, monkeypatch):
    _fake_soffice(tmp_path, monkeypatch, "text.docx")
    source = tmp_path / "in.doc"
    source.write_bytes(b"x")
    converted = legacy.convert(source, "doc", "docx", tmp_path / "work", 30)
    assert converted.read_bytes() == fixture_bytes("text.docx")
    limits = (tmp_path / "work" / "limits").read_text()
    assert _limit(limits, "Max address space") == legacy.ADDRESS_SPACE_BYTES
    assert _limit(limits, "Max file size") == legacy.MAX_FILE_BYTES


@needs_prlimit
def test_a_converted_package_over_the_zip_limits_is_limit_exceeded(tmp_path, monkeypatch):
    # A legacy file is not a zip, so only the converted package can carry a zip bomb: it gets the native file's answer.
    _fake_soffice(tmp_path, monkeypatch, "bomb.docx")
    job_dir = tmp_path / "job"
    job_dir.mkdir()
    (job_dir / "input").write_bytes(b"x")
    job = {
        "request_id": "l-2",
        "input": str(job_dir / "input"),
        "dir": str(job_dir),
        "format": "doc",
        "options": _options("l-2", ".doc"),
    }
    with pytest.raises(ParserError) as raised:
        convert.run_job(job, convert.Pipelines(tmp_path / "no-models", 1))
    assert (raised.value.code, raised.value.limit) == ("limit_exceeded", "zip_ratio")

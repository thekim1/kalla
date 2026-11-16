"""Legacy Word, Excel, PowerPoint and RTF through LibreOffice and the declarative backends (ume_parser.legacy).

The conversions need `soffice` (the parser image has it) and are skipped elsewhere; the tests of the launch and of the
output checks stand in a shell script for it and need only `prlimit`. The legacy inputs are made here by LibreOffice
itself from the repository's OOXML and RTF fixtures, so no binary legacy files are committed.
"""

import shutil
import subprocess
from pathlib import Path

import pytest
from defusedxml.ElementTree import parse as parse_xml

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


def _soffice(work: Path, source: Path, convert_to: str) -> Path:
    """`source` converted by LibreOffice (`--convert-to <ext>[:<filter>]`) with the profile `work/profile`, fresh unless
    the caller seeded it; returns the file it wrote."""
    subprocess.run(  # noqa: S603 (fixed command)
        [
            legacy.SOFFICE,
            "--headless",
            f"-env:UserInstallation={(work / 'profile').as_uri()}",
            "--convert-to",
            convert_to,
            "--outdir",
            str(work / "out"),
            str(source),
        ],
        check=True,
        capture_output=True,
        timeout=120,
        env={"HOME": str(work), "PATH": "/usr/bin:/bin"},
    )
    return work / "out" / f"{source.stem}.{convert_to.split(':', 1)[0]}"


def _make(tmp_path, source: str, fmt: str) -> bytes:
    """`source` (a fixture) saved by LibreOffice as `fmt`."""
    work = tmp_path / f"make-{fmt}"
    work.mkdir()
    (work / source).write_bytes(fixture_bytes(source))
    return _soffice(work, work / source, f"{fmt}:{_FILTERS[fmt]}").read_bytes()


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


# LibreOffice's link and macro settings (to-verify #69, #70). The image ships no LibreOffice configuration of its own
# and every conversion gets a fresh profile, so the values in effect are the defaults of the installed packages'
# registry layers (`share/registry/*.xcd`). The test below pins the ones the parser relies on, so a LibreOffice update
# that changes them fails the image build. Value meanings are LibreOffice's code, not the schema's labels: Writer's
# `Update/Link` is 0 never, 1 on request, 2 always (sw/inc/linkenum.hxx; the schema's labels call 0 "Always" and 2
# "Never"); Calc's is 0 always, 1 never, 2 on request (sc/inc/global.hxx `ScLkUpdMode`). "On request" asks a question,
# which headless LibreOffice never answers yes, so no link is updated.
_OOR = "{http://openoffice.org/2001/registry}"
_SCRIPTING = "org.openoffice.Office.Common/Security/Scripting"
_WRITER_LINKS = "org.openoffice.Office.Writer/Content/Update/Link"
_CALC_LINKS = "org.openoffice.Office.Calc/Content/Update/Link"


def _registry(xcd_dir: Path) -> dict[str, str | None]:
    """The property values of LibreOffice's shipped registry layers, keyed `<package>.<component>/<group>/…/<prop>`:
    the schemas' defaults with the data layers over them."""
    defaults: dict[str, str | None] = {}
    data: dict[str, str | None] = {}
    for xcd in sorted(xcd_dir.glob("*.xcd")):
        for component in parse_xml(xcd).getroot():
            kind = component.tag.rsplit("}", 1)[-1]
            if kind in ("component-schema", "component-data"):
                path = f"{component.get(_OOR + 'package')}.{component.get(_OOR + 'name')}"
                _collect(component, path, defaults if kind == "component-schema" else data)
    return defaults | data


def _collect(node, path: str, out: dict[str, str | None]) -> None:
    for child in node:
        name = child.get(_OOR + "name")
        if child.tag == "templates":
            continue
        if child.tag == "prop":
            value = child.find("value")
            out[f"{path}/{name}"] = None if value is None else value.text
        else:
            _collect(child, path if name is None else f"{path}/{name}", out)


def _profile(xcu: Path) -> dict[str, str | None]:
    """The values a user profile's `registrymodifications.xcu` holds, keyed like `_registry`."""
    values: dict[str, str | None] = {}
    for item in parse_xml(xcu).getroot():
        path = (item.get(_OOR + "path") or "").lstrip("/")
        for prop in item.iter("prop"):
            value = prop.find("value")
            values[f"{path}/{prop.get(_OOR + 'name')}"] = None if value is None else value.text
    return values


@needs_soffice
def test_libreoffice_runs_with_the_shipped_link_and_macro_defaults(tmp_path):
    work = tmp_path / "work"
    legacy.convert(_fixture(tmp_path, "legacy.rtf"), "rtf", "docx", work, 60)
    program = Path(shutil.which(legacy.SOFFICE) or "").resolve().parent
    # In effect: the shipped layers, then the job's fresh profile, into which LibreOffice writes some of the values it
    # read.
    registry = _registry(program.parent / "share" / "registry")
    modifications = work / "profile" / "user" / "registrymodifications.xcu"
    if modifications.exists():
        registry |= _profile(modifications)
    # High: unsigned macros never run, signed ones only from a trusted author, and there are no trusted locations.
    assert registry[f"{_SCRIPTING}/MacroSecurityLevel"] == "2"
    assert registry[f"{_SCRIPTING}/SecureURL"] is None
    assert registry[_WRITER_LINKS] == "1"
    assert registry[_CALC_LINKS] == "2"
    # No update check: the online-update component (its job `UpdateCheck`, library `updchk`) is not installed.
    assert not [key for key in registry if key.startswith("org.openoffice.Office.Jobs/Jobs/UpdateCheck/")]
    assert not list(program.glob("*updchk*"))


# The text of the planted files a fixture links to; it must never reach an output.
_MARKER = "PlantedLinkTarget7f3a"
_STORED = "Stored link result."


def _rtf_with_links(planted: Path) -> bytes:
    """An RTF whose INCLUDETEXT fields name the planted text file and /etc/passwd, each with a stored result."""
    fields = "".join(
        '\\pard {\\field{\\*\\fldinst { INCLUDETEXT "' + str(path) + '" }}{\\fldrslt {' + _STORED + "}}}\\par\n"
        for path in (planted / "linked.txt", "/etc/passwd")
    )
    head = "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Times New Roman;}}\n\\pard Before the links.\\par\n"
    return (head + fields + "\\pard After the links.\\par\n}\n").encode()


def _fods(rows: list[str]) -> str:
    cells = "".join(f"<table:table-row>{row}</table:table-row>" for row in rows)
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"'
        ' xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0"'
        ' xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"'
        ' xmlns:of="urn:oasis:names:tc:opendocument:xmlns:of:1.2" office:version="1.3"'
        ' office:mimetype="application/vnd.oasis.opendocument.spreadsheet">'
        f'<office:body><office:spreadsheet><table:table table:name="Sheet1">{cells}</table:table>'
        "</office:spreadsheet></office:body></office:document>\n"
    )


def _text_cell(text: str) -> str:
    return f'<table:table-cell office:value-type="string"><text:p>{text}</text:p></table:table-cell>'


def _xls_with_links(work: Path, planted: Path) -> bytes:
    """An XLS with an external reference and a DDE link to the planted spreadsheet and an external reference to
    /etc/passwd, saved by LibreOffice."""
    target = planted / "linked.ods"
    formulas = [
        f"of:=['{target.as_uri()}'#$Sheet1.A1]",
        f"of:=DDE(&quot;soffice&quot;;&quot;{target}&quot;;&quot;Sheet1.A1&quot;)",
        "of:=['file:///etc/passwd'#$passwd.A1]",
    ]
    rows = [_text_cell("Before the links.")] + [f'<table:table-cell table:formula="{f}"/>' for f in formulas]
    source = work / "links.fods"
    source.write_text(_fods(rows))
    return _soffice(work, source, "xls:MS Excel 97").read_bytes()


def _plant(planted: Path) -> None:
    """Writes the marker into the files the fixtures link to (after the fixtures are made, so no stored result can
    hold it)."""
    (planted / "linked.txt").write_text(_MARKER + "\n")
    work = planted / "make"
    work.mkdir()
    source = work / "linked.fods"
    source.write_text(_fods([_text_cell(_MARKER)]))
    shutil.copyfile(_soffice(work, source, "ods"), planted / "linked.ods")


@needs_soffice
@pytest.mark.parametrize("fmt", ["rtf", "doc", "xls"])
def test_no_linked_local_file_reaches_the_output(tmp_path, pipelines, fmt):
    planted = tmp_path / "planted"
    planted.mkdir()
    work = tmp_path / "make"
    work.mkdir()
    if fmt == "xls":
        data = _xls_with_links(work, planted)
        references = ["linked.ods", "passwd"]
    else:
        data = _rtf_with_links(planted)
        if fmt == "doc":
            (work / "links.rtf").write_bytes(data)
            data = _soffice(work, work / "links.rtf", "doc:MS Word 97").read_bytes()
        references = ["INCLUDETEXT", "linked.txt", "/etc/passwd"]
    # The fixture carries its links (Word reads INCLUDETEXT as a section linked to the file).
    assert all(reference.encode() in data or reference.encode("utf-16-le") in data for reference in references)
    _plant(planted)
    out = _run(tmp_path, pipelines, data, f".{fmt}")
    assert (out["status"], out["format"]) == ("success", fmt)
    assert "Before the links." in out["markdown"]
    everything = str(out)
    assert _MARKER not in everything
    assert "root:" not in everything


# A profile that makes LibreOffice update links: Writer `Update/Link` 2 (always) and macro security 0, under which every
# location is trusted for link updates (unotools `isTrustedLocationUriForUpdatingLinks`).
_UPDATING_PROFILE = """<?xml version="1.0" encoding="UTF-8"?>
<oor:items xmlns:oor="http://openoffice.org/2001/registry">
<item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse">\
<value>0</value></prop></item>
<item oor:path="/org.openoffice.Office.Writer/Content/Update"><prop oor:name="Link" oor:op="fuse">\
<value>2</value></prop></item>
</oor:items>
"""


@needs_soffice
def test_a_planted_file_reaches_the_output_only_when_links_update(tmp_path):
    """The control for the test above: the planted marker does show when LibreOffice updates a link, so its absence
    there means the link was not followed. A Writer section linked to the planted file shows the file's text with the
    updating profile and its stored text with the shipped defaults."""
    planted = tmp_path / "planted"
    planted.mkdir()
    (planted / "linked.txt").write_text(_MARKER + "\n")
    source = tmp_path / "linked.fodt"
    source.write_text(
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"'
        ' xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:xlink="http://www.w3.org/1999/xlink"'
        ' office:version="1.3" office:mimetype="application/vnd.oasis.opendocument.text">'
        "<office:body><office:text><text:p>Before the link.</text:p>"
        '<text:section text:name="Linked">'
        f'<text:section-source xlink:href="{(planted / "linked.txt").as_uri()}" xlink:type="simple"'
        ' text:filter-name="Text"/>'
        f"<text:p>{_STORED}</text:p></text:section></office:text></office:body></office:document>\n"
    )
    texts = {}
    for name, profile in (("defaults", None), ("updating", _UPDATING_PROFILE)):
        work = tmp_path / name
        (work / "profile" / "user").mkdir(parents=True)
        if profile:
            (work / "profile" / "user" / "registrymodifications.xcu").write_text(profile)
        texts[name] = _soffice(work, source, "txt:Text").read_text()
    assert _MARKER not in texts["defaults"]
    assert _STORED in texts["defaults"]
    assert _MARKER in texts["updating"]

import io
import zipfile

import pytest

from helpers import fixture_bytes
from ume_parser.errors import ParserError
from ume_parser.sniff import ZIP, classify_zip, declared_mismatch, sniff, sniff_file


def _zip(*names: str, mimetype: bytes | None = None) -> zipfile.ZipFile:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        if mimetype is not None:
            archive.writestr("mimetype", mimetype)
        for name in names:
            archive.writestr(name, b"<x/>")
    return zipfile.ZipFile(buffer)


def _open(name: str) -> zipfile.ZipFile:
    return zipfile.ZipFile(io.BytesIO(fixture_bytes(name)))


def test_pdf_is_sniffed_also_after_leading_bytes():
    assert sniff(fixture_bytes("text-layer.pdf")).format == "pdf"
    assert sniff(b"\x00" * 100 + b"%PDF-1.7\n").media_type == "application/pdf"


@pytest.mark.parametrize(
    ("name", "fmt"),
    [("scan.png", "png"), ("scan.jpg", "jpeg"), ("frames.tiff", "tiff"), ("huge.png", "png")],
)
def test_images_are_sniffed_from_their_signature(name, fmt):
    assert sniff(fixture_bytes(name)).format == fmt


@pytest.mark.parametrize("name", ["text.docx", "slides.pptx", "sheets.xlsx", "text.odt", "archive.zip"])
def test_a_zip_is_left_for_the_slot(name):
    assert sniff(fixture_bytes(name)) == ZIP


@pytest.mark.parametrize(
    ("head", "code", "reason"),
    [
        (fixture_bytes("legacy.doc"), "format_deferred", None),
        (fixture_bytes("legacy.msg"), "format_deferred", None),
        (fixture_bytes("legacy.rtf"), "format_deferred", None),
        (fixture_bytes("message.eml"), "format_deferred", None),
        (b"Received: from mx.example.org\r\n", "format_deferred", None),
        (b"7z\xbc\xaf\x27\x1c\x00\x04", "unsupported_format", "archive"),
        (b"\x1f\x8b\x08\x00", "unsupported_format", "archive"),
        (b"MZ\x90\x00", "unsupported_format", "executable"),
        (b"\x7fELF\x02\x01", "unsupported_format", "executable"),
        (b"GIF89a", "unsupported_format", "unknown"),
        (b"hello", "unsupported_format", "unknown"),
    ],
)
def test_refusals_from_the_first_bytes(head, code, reason):
    with pytest.raises(ParserError) as raised:
        sniff(head)
    assert (raised.value.code, raised.value.reason) == (code, reason)


@pytest.mark.parametrize(("name", "code"), [("encrypted.docx", "encrypted"), ("legacy.msg", "format_deferred")])
def test_an_ole_file_is_told_apart_by_its_stream_names(tmp_path, name, code):
    path = tmp_path / "input"
    data = fixture_bytes(name)
    path.write_bytes(data)
    with pytest.raises(ParserError) as raised:
        sniff_file(path, data[:1024])
    assert raised.value.code == code


@pytest.mark.parametrize(
    ("name", "fmt"),
    [
        ("text.docx", "docx"),
        ("slides.pptx", "pptx"),
        ("sheets.xlsx", "xlsx"),
        ("text.odt", "odt"),
        ("sheets.ods", "ods"),
        ("slides.odp", "odp"),
    ],
)
def test_packages_are_named_by_their_parts(name, fmt):
    assert classify_zip(_open(name)).format == fmt


@pytest.mark.parametrize(
    ("archive", "reason"),
    [
        (lambda: _open("macro.docm"), "macro_enabled"),
        (lambda: _zip("[Content_Types].xml", "xl/workbook.xml", "xl/vbaProject.bin"), "macro_enabled"),
        (lambda: _open("archive.zip"), "archive"),
        (lambda: _zip("[Content_Types].xml", "visio/document.xml"), "unknown"),
        (lambda: _zip("content.xml", mimetype=b"application/vnd.oasis.opendocument.graphics"), "unknown"),
        (lambda: _zip("content.xml", mimetype=b"x" * 500), "unknown"),
    ],
)
def test_other_zips_are_refused(archive, reason):
    with pytest.raises(ParserError) as raised:
        classify_zip(archive())
    assert (raised.value.code, raised.value.reason) == ("unsupported_format", reason)


def test_declared_type_mismatch():
    assert not declared_mismatch("jpeg", ".jpg", "image/jpeg")
    assert not declared_mismatch("tiff", ".tif", None)
    assert declared_mismatch("docx", ".pdf", None)
    assert declared_mismatch("pdf", ".pdf", "application/octet-stream")

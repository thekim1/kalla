"""The bomb and size limits of parser design §11 (zip pre-check, pixels, frames, render size, spreadsheet cells)."""

import zipfile

import pytest

from helpers import fixture_bytes
from ume_parser import limits
from ume_parser.errors import ParserError


def _entry(name: str, size: int = 10, compressed: int = 10) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name)
    info.file_size, info.compress_size = size, compressed
    return info


def _limit(call) -> str:
    with pytest.raises(ParserError) as raised:
        call()
    assert raised.value.code == "limit_exceeded"
    return raised.value.limit


@pytest.mark.parametrize(
    ("entries", "limit"),
    [
        ([_entry(f"f{i}") for i in range(limits.MAX_ZIP_ENTRIES + 1)], "zip_entries"),
        ([_entry("../../etc/passwd")], "zip_entry_name"),
        ([_entry("/etc/passwd")], "zip_entry_name"),
        ([_entry("word\\..\\x.xml")], "zip_entry_name"),
        ([_entry("C:/x.xml")], "zip_entry_name"),
        ([_entry("word/media/inner.zip")], "zip_nested"),
        ([_entry("a.bin", 300 * 1024 * 1024, 300 * 1024 * 1024)] * 2, "zip_uncompressed_bytes"),
        ([_entry("a.bin", 4 * 1024 * 1024, 4 * 1024)], "zip_ratio"),
    ],
)
def test_zip_precheck_refuses(entries, limit):
    assert _limit(lambda: limits.check_zip(entries)) == limit


def test_zip_precheck_accepts_packages_and_their_embeddings():
    limits.check_zip(
        [
            _entry("[Content_Types].xml", 2000, 10),  # small parts compress far, below the ratio floor
            _entry("word/document.xml", 900_000, 50_000),
            _entry("word/embeddings/Microsoft_Excel_Worksheet.xlsx", 9000, 8000),
        ]
    )


def test_the_bomb_fixture_is_refused_by_its_ratio(tmp_path):
    path = tmp_path / "bomb.docx"
    path.write_bytes(fixture_bytes("bomb.docx"))
    with limits.open_zip(path) as archive:
        assert _limit(lambda: limits.check_zip(archive.infolist())) == "zip_ratio"


def test_not_a_zip_is_corrupt(tmp_path):
    path = tmp_path / "x"
    path.write_bytes(b"PK\x03\x04" + bytes(40))
    with pytest.raises(ParserError) as raised:
        limits.open_zip(path)
    assert raised.value.code == "corrupt"


def test_image_pixel_limit_is_hard(tmp_path):
    path = tmp_path / "huge.png"
    path.write_bytes(fixture_bytes("huge.png"))
    assert _limit(lambda: limits.open_image(path)) == "image_pixels"


def test_image_frames_are_counted(tmp_path, monkeypatch):
    path = tmp_path / "frames.tiff"
    path.write_bytes(fixture_bytes("frames.tiff"))
    with limits.open_image(path) as image:
        assert image.n_frames == 3
    monkeypatch.setattr(limits, "MAX_TIFF_FRAMES", 2)
    assert _limit(lambda: limits.open_image(path)) == "tiff_frames"


def test_broken_image_is_corrupt(tmp_path):
    path = tmp_path / "broken.png"
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + bytes(20))
    with pytest.raises(ParserError) as raised:
        limits.open_image(path)
    assert raised.value.code == "corrupt"

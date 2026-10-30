import pytest

from ume_parser.settings import Settings


@pytest.fixture
def settings(tmp_path) -> Settings:
    return Settings(jobs_dir=tmp_path / "jobs", workers=1, threads=2)

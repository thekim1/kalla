"""Deployment settings, read from `PARSER_*` environment variables (parser design §10.1, §12)."""

from pathlib import Path

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="PARSER_", frozen=True)

    host: str = "0.0.0.0"  # noqa: S104 (the container listens on its own network only)
    port: int = 8000
    workers: int = Field(default=1, ge=1, description="pre-warmed conversion slots")
    threads: int = Field(default=4, ge=1, description="torch and OpenMP threads per slot")
    models_dir: Path = Path("/opt/models")
    jobs_dir: Path = Path("/tmp/jobs")  # noqa: S108 (a private tmpfs in the container, parser design §10.3)
    warmup_timeout_s: float = Field(default=600.0, gt=0, description="a slot not warm by then is killed and respawned")
    revision: str = Field(default="unknown", description="git revision of the build, part of build_hash")
    tls_cert_file: Path | None = None
    tls_key_file: Path | None = None

    @property
    def tls(self) -> bool:
        return self.tls_cert_file is not None and self.tls_key_file is not None

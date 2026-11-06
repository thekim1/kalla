"""Deployment settings, read from `PARSER_*` and `NER_*` environment variables (parser design §7.8, §10, §12)."""

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
    slot_max_rss_mb: int = Field(default=2800, ge=64, description="RSS watchdog: a slot above it is killed (§10.3)")
    recycle_jobs: int = Field(default=50, ge=1, description="a slot is respawned after this many conversions")
    recycle_rss_growth: float = Field(
        default=0.4, gt=0, description="a slot is respawned when its idle RSS exceeds its warm RSS by this share"
    )
    idle_stop_s: float = Field(
        default=900.0, ge=0, description="an idle convert slot stops after this; the next request warms it (0: never)"
    )
    pipeline_cache: int = Field(default=3, ge=1, description="Docling option sets kept warm per slot (§10.2)")

    @property
    def tls(self) -> bool:
        return self.tls_cert_file is not None and self.tls_key_file is not None


class NerSettings(BaseSettings):
    """`ner-0` (§7.8). With `NER_IDLE_STOP_S` > 0 it starts on the first `/v1/ner` request (answered `not_ready`
    while it warms) and stops after that long without one; 0 keeps it warm from the start. `NER_ENABLED=false` starts
    no NER process: `/v1/ner` answers `not_ready` and `/v1/info`
    `ner_ready: false`, so the worker holds the sets that need a name check (processing design §9.6)."""

    model_config = SettingsConfigDict(env_prefix="NER_", frozen=True)

    enabled: bool = True
    threads: int = Field(default=2, ge=1)
    quantize: bool = Field(default=True, description="int8 dynamic quantisation of the model's linear layers")
    queue: int = Field(default=4, ge=0, description="requests that may wait for ner-0; more get busy")
    person_labels: str = Field(default="PER,PRS", description="model labels that are persons, comma-separated")
    recycle_requests: int = Field(default=5000, ge=1)
    idle_stop_s: float = Field(
        default=900.0,
        ge=0,
        description="ner-0 starts on the first request and stops after this long without one (0: warm from start)",
    )

    @property
    def person_label_list(self) -> list[str]:
        return [label.strip() for label in self.person_labels.split(",") if label.strip()]

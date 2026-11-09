"""The parser contract `parser/v1` (parser design §7): request options, results, info and errors as pydantic models.

`scripts/export_schema.py` writes their JSON Schemas to `contracts/parser/v1/`; CI fails when the committed schemas
differ. `/v1` changes are additive only (§7.7): new optional request fields, new result fields, new enum values.

- **Requests** reject unknown properties (`extra="forbid"`, §7.2), so drift shows at once as `invalid_request`.
- **Results** list every field as required, with `null` where a value does not apply, and do not forbid additional
  properties, so a consumer built against an older `/v1` keeps validating newer responses.
- Python keyword clashes use aliases (`from`); responses are serialised by alias.
"""

from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator

from ume_parser import limits

CONTRACT = "parser/v1"

Pipeline = Literal["text_layer", "docling"]
Format = Literal["pdf", "docx", "pptx", "xlsx", "odt", "ods", "odp", "jpeg", "png", "tiff", "doc", "xls", "ppt", "rtf"]
BlockKind = Literal[
    "title",
    "heading",
    "paragraph",
    "list_item",
    "table",
    "picture",
    "caption",
    "code",
    "formula",
    "footnote",
    "page_header",
    "page_footer",
]
LevelSource = Literal["style", "layout", "font_size", "html", "markdown"]
Layer = Literal["body", "furniture"]
OcrApplied = Literal["none", "bitmap", "full-page"]
Retry = Literal["none", "later", "same", "smaller_range"]
ErrorCode = Literal[
    "invalid_request",
    "duplicate_in_flight",
    "too_large",
    "format_deferred",
    "unsupported_format",
    "encrypted",
    "corrupt",
    "too_many_pages",
    "limit_exceeded",
    "ocr_failed",
    "conversion_failed",
    "timeout",
    "worker_crashed",
    "resource_limit",
    "busy",
    "not_ready",
    "not_implemented",
]
UnsupportedReason = Literal["archive", "macro_enabled", "executable", "unknown"]
BBox = Annotated[list[float], Field(min_length=4, max_length=4, description="left, top, right, bottom in points")]


class _Request(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, validate_by_alias=True, validate_by_name=True)


class _Result(BaseModel):
    model_config = ConfigDict(
        json_schema_serialization_defaults_required=True,
        serialize_by_alias=True,
        validate_by_alias=True,
        validate_by_name=True,
    )


# --- Requests -------------------------------------------------------------------------------------------------------


class PageRange(_Request):
    """1-based, inclusive page numbers of the original file."""

    from_: int = Field(alias="from", ge=1)
    to: int = Field(ge=1)

    @model_validator(mode="after")
    def _ordered(self) -> "PageRange":
        if self.to < self.from_:
            raise ValueError("pages.to is before pages.from")
        return self

    @property
    def count(self) -> int:
        return self.to - self.from_ + 1


class OcrOptions(_Request):
    mode: Literal["off", "auto", "force"] = "auto"
    languages: list[Literal["swe"]] = Field(default=["swe"], min_length=1, description="Tesseract `swe` only (§9)")


class TableOptions(_Request):
    mode: Literal["fast", "accurate", "off"] = "accurate"


class OutputOptions(_Request):
    docling_json: bool = True
    markdown: bool = False


class ConvertRequest(_Request):
    """The `options` part of `POST /v1/convert` (§7.2). `pipeline: text_layer` ignores `ocr`, `tables` and
    `image_scale` and returns no `docling` part."""

    model_config = ConfigDict(title="ConvertRequest")

    request_id: str = Field(
        pattern=r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", description="job id + range ordinal: log correlation"
    )
    file_ext: str = Field(pattern=r"^\.[a-z0-9]{1,10}$", description="extension only, never the file name")
    media_type: str | None = Field(default=None, max_length=255, description="declared; compared with the sniffed type")
    pages: PageRange | None = Field(default=None, description="null = the whole document")
    deadline_ms: int = Field(default=110_000, ge=1_000, le=limits.MAX_DEADLINE_MS)
    pipeline: Pipeline = "docling"
    ocr: OcrOptions = OcrOptions()
    tables: TableOptions = TableOptions()
    image_scale: float = Field(default=2.0, ge=1.0, le=4.0)
    outputs: OutputOptions = OutputOptions()


# --- Convert result -------------------------------------------------------------------------------------------------


class PageSpan(_Result):
    from_: int = Field(alias="from")
    to: int


class ConvertInput(_Result):
    format: Format
    media_type_sniffed: str
    sha256: str = Field(pattern=r"^[0-9a-f]{64}$")
    bytes: int
    page_count: int | None
    pages_requested: PageSpan | None
    pages_completed: PageSpan | None
    pipeline: Pipeline = Field(description="the pipeline that ran (text_layer falls back to docling, §8)")


class OcrConfidence(_Result):
    mean: float
    p10: float


class PageSignals(_Result):
    """Per-page quality signals (§6.4). Character counts are non-whitespace characters."""

    chars: int
    furniture_chars: int
    garbled_score: float = Field(ge=0, le=1)
    text_layer_chars: int | None
    image_coverage: float | None
    ocr_applied: OcrApplied
    ocr_confidence: OcrConfidence | None
    table_count: int
    table_area_ratio: float
    picture_area_ratio: float
    empty: bool
    ms: int | None


class Page(_Result):
    page: int | None
    label: str | None
    width: float | None
    height: float | None
    signals: PageSignals


class Block(_Result):
    id: str
    kind: BlockKind
    level: int | None
    level_source: LevelSource | None
    page: int | None
    page_end: int | None
    bbox: BBox | None
    layer: Layer
    md: str
    ref: str | None = Field(description="JSON pointer into the DoclingDocument; null on the text-layer path")
    table: str | None = Field(description="id in `tables` for a table block")


class TableCell(_Result):
    r: int
    c: int
    rs: int
    cs: int
    header: bool = Field(description="a column or row header cell")
    text: str


class Table(_Result):
    id: str
    rows: int
    cols: int
    header_rows: int
    caption: str | None
    cells: list[TableCell]


class Picture(_Result):
    id: str
    block: str
    page: int | None
    bbox: BBox | None
    caption: str | None
    ocr_chars: int


class ConvertWarning(_Result):
    code: str
    page: int | None


class Timings(_Result):
    total_ms: int
    wait_ms: int
    docling_ms: int
    text_layer_ms: int
    signals_ms: int


class Build(_Result):
    parser: str
    docling: str
    build_hash: str


class ConvertResult(_Result):
    """`result.json`, the first part of a `/v1/convert` response (§7.3)."""

    model_config = ConfigDict(title="ConvertResult")

    contract: Literal["parser/v1"]
    request_id: str
    status: Literal["success", "partial"]
    input: ConvertInput
    pages: list[Page]
    blocks: list[Block]
    tables: list[Table]
    pictures: list[Picture]
    warnings: list[ConvertWarning]
    timings: Timings
    build: Build
    markdown: str | None = Field(description="the document Markdown with page markers, when outputs.markdown is set")


# --- Inspect --------------------------------------------------------------------------------------------------------


class InspectRequest(_Request):
    """The optional `options` part of `POST /v1/inspect` (§6.2): what the worker declares, for the mismatch warning."""

    model_config = ConfigDict(title="InspectRequest")

    request_id: str | None = Field(default=None, pattern=r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
    file_ext: str | None = Field(default=None, pattern=r"^\.[a-z0-9]{1,10}$")
    media_type: str | None = Field(default=None, max_length=255)


class InspectPage(_Result):
    """A PDF page (text-layer facts, no rendering) or an image frame (size and resolution only)."""

    page: int
    width: float = Field(description="points")
    height: float = Field(description="points")
    rotation: int | None
    text_chars: int | None
    bad_chars: int | None
    garbled_score: float | None
    image_coverage: float | None
    path_objects: int | None
    dpi: float | None = Field(description="image frames: the resolution the size in points is computed from")


class InspectTimings(_Result):
    total_ms: int
    wait_ms: int
    inspect_ms: int


class InspectResult(_Result):
    """`POST /v1/inspect` (§6.2): cheap facts and per-page signals, no models."""

    model_config = ConfigDict(title="InspectResult")

    contract: Literal["parser/v1"]
    request_id: str | None
    format: Format
    media_type_sniffed: str
    page_count: int | None
    rangeable: bool
    encrypted: Literal["none", "owner_only", "user_password"] | None
    has_javascript: bool | None
    has_embedded_files: bool | None
    has_xfa: bool | None
    pages: list[InspectPage]
    refusal: ErrorCode | None = Field(description="the error code convert would give, if any")
    warnings: list[ConvertWarning]
    timings: InspectTimings
    build: Build


# --- NER (§7.8; processing design §10) --------------------------------------------------------------------------------

NerLabel = Literal["person", "person_ambiguous"]


class NerText(_Request):
    id: str = Field(pattern=r"^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$")
    text: str = Field(max_length=limits.NER_MAX_TEXT_CHARS, description="NFC; offsets in the result are code points")


class NerRequest(_Request):
    """`POST /v1/ner`: text units of one chunk set. `person_ambiguous` (mixed labels such as `ORG/PRS`) is returned only
    when asked for."""

    model_config = ConfigDict(title="NerRequest")

    request_id: str = Field(pattern=r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
    texts: list[NerText] = Field(min_length=1, max_length=limits.NER_MAX_TEXTS)
    labels: list[NerLabel] = Field(default=["person"], min_length=1)
    min_score: float = Field(default=0.0, ge=0, le=1)
    deadline_ms: int = Field(default=limits.NER_DEADLINE_MS, ge=1_000, le=limits.NER_DEADLINE_MS)

    @model_validator(mode="after")
    def _bounded(self) -> "NerRequest":
        if sum(len(t.text) for t in self.texts) > limits.NER_MAX_REQUEST_CHARS:
            raise ValueError("texts exceed the characters per request")
        if len({t.id for t in self.texts}) != len(self.texts):
            raise ValueError("text ids are not unique")
        return self


class NerEntity(_Result):
    text_id: str
    start: int = Field(ge=0, description="code point offset into the text as sent")
    end: int = Field(ge=0, description="exclusive")
    label: NerLabel
    score: float = Field(ge=0, le=1)


class NerModel(_Result):
    repository: str
    revision: str
    sha256: str = Field(description="of the weights file")
    labels: list[str] = Field(description="the model's own labels")
    person_labels: list[str] = Field(description="model labels mapped to `person`")
    quantised: bool


class NerTimings(_Result):
    total_ms: int
    wait_ms: int
    ner_ms: int


class NerResult(_Result):
    """The answer of `POST /v1/ner`. Entity text is never echoed back."""

    model_config = ConfigDict(title="NerResult")

    contract: Literal["parser/v1"]
    request_id: str
    entities: list[NerEntity]
    model: NerModel
    timings: NerTimings
    build: Build


# --- Info -----------------------------------------------------------------------------------------------------------


class ParserVersion(_Result):
    version: str
    revision: str


class Pdfium(_Result):
    build: int
    origin: str
    flags: list[str]


class Tesseract(_Result):
    version: str
    languages: list[str]


class Model(_Result):
    name: str
    repository: str
    revision: str
    licence: str
    files: dict[str, str] = Field(description="path → SHA-256")


class Limits(_Result):
    max_body_bytes: int
    max_pages_per_request: int
    max_pages_per_document: int
    max_deadline_ms: int
    inspect_deadline_ms: int
    max_zip_entries: int
    max_zip_uncompressed_bytes: int
    max_zip_ratio: int
    max_image_pixels: int
    max_tiff_frames: int
    max_render_pixels: int
    max_spreadsheet_cells: int
    ner_max_texts: int
    ner_max_text_chars: int
    ner_max_request_chars: int
    ner_deadline_ms: int


class Formats(_Result):
    supported: list[str]
    deferred: list[str]


class StaticInfo(_Result):
    """Everything in `/v1/info` that `build_hash` covers (§13.1)."""

    parser: ParserVersion
    python: str
    packages: dict[str, str]
    pdfium: Pdfium
    tesseract: Tesseract | None
    models: list[Model]
    ner: NerModel | None = Field(description="the NER model as `ner-0` loads it; null when NER is off")
    limits: Limits
    formats: Formats


class Capacity(_Result):
    convert_slots: int
    slots_ready: int
    inspect_slots: int
    ner_ready: bool


class Info(StaticInfo):
    """`GET /v1/info`."""

    model_config = ConfigDict(title="Info")

    build_hash: str = Field(pattern=r"^sha256:[0-9a-f]{64}$", description="SHA-256 of everything except capacity")
    capacity: Capacity


# --- Errors ---------------------------------------------------------------------------------------------------------


class ErrorBody(_Result):
    code: ErrorCode
    detail: str = Field(description="a fixed sentence per code, never a library message")
    retry: Retry
    limit: str | None = Field(description="which limit, for limit_exceeded")
    reason: UnsupportedReason | None = Field(description="for unsupported_format")
    pages_completed: PageSpan | None


class ErrorResponse(_Result):
    """Every non-200 response of `/v1/convert`, `/v1/inspect` and `/v1/ner` (§7.5)."""

    model_config = ConfigDict(title="ErrorResponse")

    error: ErrorBody
    request_id: str | None
    build: Build

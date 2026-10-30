"""Docling pipelines of a conversion slot. Imported only in slot processes, never by the API process."""

from pathlib import Path

from docling.datamodel.accelerator_options import AcceleratorDevice, AcceleratorOptions
from docling.datamodel.base_models import InputFormat
from docling.datamodel.pipeline_options import (
    OcrMode,
    PdfPipelineOptions,
    TableFormerMode,
    TableStructureOptions,
    TesseractCliOcrOptions,
)
from docling.document_converter import DocumentConverter, PdfFormatOption

OCR_LANGUAGES = ["swe"]
DEFAULT_IMAGE_SCALE = 2.0


def default_pdf_options(models_dir: Path, threads: int) -> PdfPipelineOptions:
    """The default option set (parser design §10.2): OCR `auto` with Tesseract `swe`, tables `accurate`, scale 2.0.

    The OCR engine is always set explicitly. Docling's default `OcrAutoOptions` never picks Tesseract (§9.2).
    """
    return PdfPipelineOptions(
        artifacts_path=models_dir,
        accelerator_options=AcceleratorOptions(num_threads=threads, device=AcceleratorDevice.CPU),
        do_ocr=True,
        ocr_options=TesseractCliOcrOptions(lang=OCR_LANGUAGES, mode=OcrMode.DEFAULT),
        do_table_structure=True,
        table_structure_options=TableStructureOptions(mode=TableFormerMode.ACCURATE),
        images_scale=DEFAULT_IMAGE_SCALE,
    )


def build_converter(models_dir: Path, threads: int) -> DocumentConverter:
    converter = DocumentConverter(
        allowed_formats=[InputFormat.PDF],
        format_options={InputFormat.PDF: PdfFormatOption(pipeline_options=default_pdf_options(models_dir, threads))},
    )
    converter.initialize_pipeline(InputFormat.PDF)
    return converter

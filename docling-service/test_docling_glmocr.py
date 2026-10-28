"""Docling using GLM-OCR as its VLM backend, instead of the default pipeline."""
import sys
import time

from docling.datamodel.base_models import InputFormat
from docling.document_converter import DocumentConverter, PdfFormatOption
from docling.pipeline.vlm_pipeline import VlmPipeline
from docling.datamodel.pipeline_options import VlmPipelineOptions
from docling.datamodel import vlm_model_specs

if len(sys.argv) < 2:
    print("Usage: test_docling_glmocr.py <path-to-pdf>")
    sys.exit(1)

pdf_path = sys.argv[1]
print(f"Converting with GLM-OCR (via Docling VLM pipeline): {pdf_path}")

pipeline_options = VlmPipelineOptions(
    vlm_options=vlm_model_specs.GLMOCR_TRANSFORMERS,
)
converter = DocumentConverter(
    format_options={
        InputFormat.PDF: PdfFormatOption(
            pipeline_cls=VlmPipeline,
            pipeline_options=pipeline_options,
        ),
    }
)

t0 = time.time()
result = converter.convert(pdf_path)
elapsed = time.time() - t0

markdown = result.document.export_to_markdown()
print(f"\n--- Done in {elapsed:.1f}s ---")
print(f"Pages: {result.document.num_pages()}")
print(f"Markdown length: {len(markdown)} chars")
print("\n--- First 2000 chars of output ---")
print(markdown[:2000])

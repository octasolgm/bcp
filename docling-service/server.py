"""
Minimal local HTTP wrapper around Docling, for the .NET backend to call.

Two pipelines, selected per-request via ?mode=light|glm:
  - light: Docling's default pipeline (layout model + RapidOCR under the hood). Fast — seconds per page.
  - glm:   Docling's VLM pipeline using GLM-OCR. Far more accurate on real testing, but ~21 minutes per
           page on CPU (no GPU here) — only practical for a handful of pages at a time right now.

Not a production service — local testing only, matching "try it locally first" before any real
integration decision. Run with: venv/Scripts/python.exe server.py
"""
import tempfile
import os
import time

from fastapi import FastAPI, UploadFile, File, Query
from docling.document_converter import DocumentConverter, PdfFormatOption
from docling.datamodel.base_models import InputFormat
from docling.pipeline.vlm_pipeline import VlmPipeline
from docling.datamodel.pipeline_options import VlmPipelineOptions
from docling.datamodel import vlm_model_specs

app = FastAPI(title="Docling local test service")

# Built once per mode, on first use — loading model weights is the expensive part, never redo it per request.
_converters: dict[str, DocumentConverter] = {}


def get_converter(mode: str) -> DocumentConverter:
    if mode in _converters:
        return _converters[mode]

    if mode == "light":
        converter = DocumentConverter()
    elif mode == "glm":
        pipeline_options = VlmPipelineOptions(vlm_options=vlm_model_specs.GLMOCR_TRANSFORMERS)
        converter = DocumentConverter(
            format_options={
                InputFormat.PDF: PdfFormatOption(
                    pipeline_cls=VlmPipeline,
                    pipeline_options=pipeline_options,
                ),
            }
        )
    else:
        raise ValueError(f"Unknown mode '{mode}' — use 'light' or 'glm'.")

    _converters[mode] = converter
    return converter


@app.get("/health")
def health():
    return {"status": "ok"}


@app.post("/convert")
async def convert(mode: str = Query(...), file: UploadFile = File(...)):
    """Convert one PDF to markdown. mode=light (fast) or mode=glm (slow, most accurate)."""
    suffix = os.path.splitext(file.filename or "document.pdf")[1] or ".pdf"
    with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as tmp:
        tmp.write(await file.read())
        tmp_path = tmp.name

    try:
        converter = get_converter(mode)
        t0 = time.time()
        result = converter.convert(tmp_path)
        elapsed = time.time() - t0

        markdown = result.document.export_to_markdown()
        return {
            "mode": mode,
            "pages": result.document.num_pages(),
            "elapsedSeconds": round(elapsed, 1),
            "markdown": markdown,
        }
    finally:
        os.unlink(tmp_path)


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=5055)

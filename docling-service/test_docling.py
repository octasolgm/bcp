"""Quick smoke test: does docling's core conversion API actually work end to end?"""
import sys
import time

from docling.document_converter import DocumentConverter

if len(sys.argv) < 2:
    print("Usage: test_docling.py <path-to-pdf>")
    sys.exit(1)

pdf_path = sys.argv[1]
print(f"Converting: {pdf_path}")

t0 = time.time()
converter = DocumentConverter()
result = converter.convert(pdf_path)
elapsed = time.time() - t0

markdown = result.document.export_to_markdown()
print(f"\n--- Done in {elapsed:.1f}s ---")
print(f"Pages: {result.document.num_pages()}")
print(f"Markdown length: {len(markdown)} chars")
print("\n--- First 2000 chars of output ---")
print(markdown[:2000])

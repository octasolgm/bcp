"""Extract just page 2 (the table-of-contents page we already compared Tesseract/RapidOCR on) into its own 1-page PDF, for a fast GLM-OCR sanity check."""
import sys
import pypdfium2 as pdfium

src = sys.argv[1] if len(sys.argv) > 1 else "../bcp-api/Data/seed-docs/TFS Guidelines.pdf"
out = sys.argv[2] if len(sys.argv) > 2 else "single_page.pdf"
page_index = int(sys.argv[3]) if len(sys.argv) > 3 else 1  # 0-based, so 1 = page 2

pdf = pdfium.PdfDocument(src)
new_pdf = pdfium.PdfDocument.new()
new_pdf.import_pages(pdf, [page_index])
new_pdf.save(out)
print(f"Wrote {out} (page {page_index + 1} of {src})")

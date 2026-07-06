import { jsPDF } from 'jspdf';
import ExcelJS from 'exceljs';
import type { DualVerifyReportItem, DualVerifyReportSummary } from './dual-verify-report';

function downloadBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

function addWrappedText(doc: jsPDF, text: string, x: number, y: number, maxWidth: number): number {
  const lines = doc.splitTextToSize(text, maxWidth);
  doc.text(lines, x, y);
  return y + lines.length * 5;
}

export async function exportSummaryPdf(items: DualVerifyReportItem[], summary: DualVerifyReportSummary): Promise<void> {
  const doc = new jsPDF();
  let y = 20;
  doc.setFontSize(14);
  doc.text('Dual Verify — Executive Summary', 14, y);
  y += 10;
  doc.setFontSize(10);
  y = addWrappedText(
    doc,
    `Total: ${summary.total} · Aligned: ${summary.aligned} · Review: ${summary.needsReview} · Failed: ${summary.failed}`,
    14,
    y,
    180,
  );
  y += 10;
  for (const item of items.filter((i) => i.agreement)) {
    if (y > 270) {
      doc.addPage();
      y = 20;
    }
    y = addWrappedText(doc, `${item.pointId} — ${item.agreement!.label}: ${item.agreement!.summary}`, 14, y, 180);
    y += 4;
  }
  doc.save('dual-verify-summary.pdf');
}

export async function exportBothPassesPdf(items: DualVerifyReportItem[]): Promise<void> {
  const doc = new jsPDF();
  let y = 20;
  for (const item of items) {
    if (y > 240) {
      doc.addPage();
      y = 20;
    }
    doc.setFontSize(12);
    doc.text(`Point ${item.pointId}`, 14, y);
    y += 8;
    doc.setFontSize(9);
    if (item.landingMessage) {
      y = addWrappedText(doc, `Pass 1 (Landing AI):\n${item.landingMessage}`, 14, y, 180);
      y += 6;
    }
    if (item.llmMessage) {
      y = addWrappedText(doc, `Pass 2 (LLM):\n${item.llmMessage}`, 14, y, 180);
      y += 10;
    }
  }
  doc.save('dual-verify-both-passes.pdf');
}

export async function exportDualVerifyExcel(items: DualVerifyReportItem[]): Promise<void> {
  const wb = new ExcelJS.Workbook();
  const ws = wb.addWorksheet('Dual Verify');
  ws.addRow([
    'Point ID',
    'Agreement',
    'Landing Status',
    'LLM Status',
    'Pass 1 — Landing AI',
    'Pass 2 — LLM Verify',
  ]);
  for (const item of items) {
    ws.addRow([
      item.pointId,
      item.agreement?.label ?? '',
      item.agreement?.landingStatus ?? '',
      item.agreement?.llmStatus ?? '',
      item.landingMessage ?? '',
      item.llmMessage ?? '',
    ]);
  }
  ws.columns.forEach((col) => {
    col.width = 24;
  });
  const buffer = await wb.xlsx.writeBuffer();
  downloadBlob(new Blob([buffer]), 'dual-verify-both-passes.xlsx');
}

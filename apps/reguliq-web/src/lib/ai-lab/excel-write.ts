import type { ReferenceComplianceBlock } from './parse-reference-response';

export function normalizeMultiline(text: string): string {
  return text.replace(/\r\n/g, '\n').replace(/\r/g, '\n').trim();
}

export function comparePointOrder(
  a: ReferenceComplianceBlock,
  b: ReferenceComplianceBlock,
): number {
  const idA = (a.title.match(/^[\d.]+/)?.[0] ?? a.title).trim();
  const idB = (b.title.match(/^[\d.]+/)?.[0] ?? b.title).trim();
  const partsA = idA.split('.').map((p) => Number(p) || 0);
  const partsB = idB.split('.').map((p) => Number(p) || 0);
  const len = Math.max(partsA.length, partsB.length);
  for (let i = 0; i < len; i++) {
    const diff = (partsA[i] ?? 0) - (partsB[i] ?? 0);
    if (diff !== 0) return diff;
  }
  return a.title.localeCompare(b.title);
}

function applyWrapText(row: import('exceljs').Row): void {
  row.eachCell((cell) => {
    cell.alignment = { wrapText: true, vertical: 'top' };
  });
}

export async function downloadExcelRows(
  filename: string,
  sheetName: string,
  headers: string[],
  rows: string[][],
  colWidths: number[],
): Promise<void> {
  const ExcelJS = (await import('exceljs')).default;
  const workbook = new ExcelJS.Workbook();
  const sheet = workbook.addWorksheet(sheetName);
  sheet.columns = colWidths.map((width) => ({ width }));

  const headerRow = sheet.addRow(headers);
  applyWrapText(headerRow);

  for (const values of rows) {
    const row = sheet.addRow(values);
    applyWrapText(row);
  }

  const buffer = await workbook.xlsx.writeBuffer();
  const blob = new Blob([buffer], {
    type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  document.body.appendChild(anchor);
  anchor.click();
  document.body.removeChild(anchor);
  URL.revokeObjectURL(url);
}

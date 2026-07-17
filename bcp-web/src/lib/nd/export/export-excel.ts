import ExcelJS from "exceljs";
import type { ResultsData } from "../types";
import { formatDate, parsePointSnapshot } from "../utils";

export async function exportResultsExcel(data: ResultsData): Promise<void> {
  const wb = new ExcelJS.Workbook();
  const ws = wb.addWorksheet("Results");

  ws.columns = [
    { header: "Point #", key: "pointNumber", width: 12 },
    { header: "Title", key: "pointTitle", width: 30 },
    { header: "Content", key: "pointContent", width: 50 },
    { header: "Status", key: "status", width: 18 },
    { header: "Action Plan", key: "actionPlan", width: 50 },
    { header: "Analysis Result", key: "result", width: 50 },
  ];

  ws.getRow(1).font = { bold: true };

  for (const p of data.points) {
    const snap = parsePointSnapshot(p.pointSnapshot);
    ws.addRow({
      pointNumber: snap.pointNumber ?? "",
      pointTitle: snap.pointTitle ?? "",
      pointContent: snap.pointContent ?? "",
      status: p.finalStatus?.replace(/_/g, " ") ?? "",
      actionPlan: p.finalActionPlan ?? p.originalAiActionPlan ?? "",
      result: p.landingAiResult ?? "",
    });
  }

  const meta = wb.addWorksheet("Run Info");
  meta.addRow(["Name", data.run.name]);
  meta.addRow(["Status", data.run.status]);
  meta.addRow(["Created", formatDate(data.run.createdAt)]);
  meta.addRow(["Created By", data.run.createdByName ?? ""]);
  meta.addRow(["Points", `${data.run.processedPointsCount} / ${data.run.totalPointsCount}`]);

  const buffer = await wb.xlsx.writeBuffer();
  const blob = new Blob([buffer], {
    type: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
  });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = `${data.run.name.replace(/[^a-z0-9]/gi, "_")}_results.xlsx`;
  a.click();
  URL.revokeObjectURL(url);
}

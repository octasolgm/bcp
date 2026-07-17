import { jsPDF } from "jspdf";
import type { ResultsData } from "@/lib/types";
import { formatDate, parsePointSnapshot } from "@/lib/utils";

export function exportResultsPdf(data: ResultsData): void {
  const doc = new jsPDF();
  const margin = 14;
  let y = 20;
  const lineHeight = 7;
  const pageHeight = doc.internal.pageSize.height;

  function addLine(text: string, fontSize = 10) {
    doc.setFontSize(fontSize);
    const lines = doc.splitTextToSize(text, 180);
    for (const line of lines) {
      if (y > pageHeight - 20) {
        doc.addPage();
        y = 20;
      }
      doc.text(line, margin, y);
      y += lineHeight;
    }
  }

  addLine(data.run.name, 16);
  y += 4;
  addLine(`Status: ${data.run.status.replace(/_/g, " ")}`, 10);
  addLine(`Created: ${formatDate(data.run.createdAt)} by ${data.run.createdByName ?? "—"}`, 10);
  addLine(`Points: ${data.run.processedPointsCount} / ${data.run.totalPointsCount}`, 10);
  y += 6;

  for (const p of data.points) {
    const snap = parsePointSnapshot(p.pointSnapshot);
    addLine(`${snap.pointNumber ?? ""} ${snap.pointTitle ?? ""}`, 12);
    if (snap.pointContent) addLine(snap.pointContent, 9);
    if (p.finalStatus) addLine(`Status: ${p.finalStatus.replace(/_/g, " ")}`, 9);
    const plan = p.finalActionPlan ?? p.originalAiActionPlan;
    if (plan) {
      addLine("Action plan:", 9);
      addLine(plan, 9);
    }
    y += 4;
  }

  doc.save(`${data.run.name.replace(/[^a-z0-9]/gi, "_")}_results.pdf`);
}

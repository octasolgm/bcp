import type { ParsedComplianceResult, ReportStats } from './parse-compliance-results';
import {
  buildTierCounts,
  COLOR_LEGEND,
  getComplianceColorTier,
  TIER_EXPORT,
} from './color-tier';

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function resultToPlainText(item: ParsedComplianceResult): string {
  const parts: string[] = [];
  if (item.title) parts.push(item.title);
  if (item.body) parts.push(item.body);
  for (const field of item.fields) {
    parts.push(`${field.label} :`);
    parts.push(field.value || '—');
  }
  return parts.join('\n\n');
}

function resultToHtml(item: ParsedComplianceResult): string {
  const tier = getComplianceColorTier(item);
  const style = TIER_EXPORT[tier];

  const parts: string[] = [`<div style="${style.wrap}">`];

  parts.push(
    `<p style="display:inline-block;margin:0 0 14px;padding:4px 10px;font-size:11px;font-weight:bold;color:${style.badge};text-transform:uppercase;letter-spacing:0.04em;border:1px solid ${style.badgeBorder};background:#fff;border-radius:4px;">${style.badgeLabel}</p>`,
  );

  if (item.title) {
    parts.push(
      `<h2 style="margin:0 0 8px;font-size:18px;font-weight:bold;color:${style.title};">${escapeHtml(item.title)}</h2>`,
    );
  }

  if (item.body) {
    parts.push(
      `<p style="margin:0 0 16px;font-size:14px;line-height:1.6;color:#1e293b;white-space:pre-wrap;">${escapeHtml(item.body)}</p>`,
    );
  }

  for (const field of item.fields) {
    const isConfidence = field.label.includes('Confidence');
    const isStatus = field.label.includes('Status');
    const fieldStyle =
      isConfidence || isStatus
        ? 'margin-bottom:12px;padding:8px;border:1px solid #cbd5e1;background:rgba(255,255,255,0.6);border-radius:6px;'
        : 'margin-bottom:12px;';

    parts.push(`<div style="${fieldStyle}">`);
    parts.push(
      `<p style="margin:0;font-size:14px;font-weight:bold;color:#0f172a;">${escapeHtml(field.label)} :</p>`,
    );
    const valueColor =
      isConfidence || isStatus ? style.value : '#334155';
    const valueWeight = isConfidence || isStatus ? 'bold' : 'normal';
    parts.push(
      `<p style="margin:4px 0 0;font-size:14px;line-height:1.6;color:${valueColor};font-weight:${valueWeight};white-space:pre-wrap;">${escapeHtml(field.value || '—')}</p>`,
    );
    parts.push('</div>');
  }

  parts.push('</div>');
  return parts.join('');
}

export function buildFormattedResultsPlain(
  results: ParsedComplianceResult[],
): string {
  return results.map(resultToPlainText).join('\n\n---\n\n');
}

export function buildFormattedResultsHtml(
  results: ParsedComplianceResult[],
): string {
  return results.map(resultToHtml).join('');
}

export function buildStatsPlain(stats: ReportStats): string {
  return [
    'COMPLIANCE REPORT STATISTICS',
    '============================',
    `Total points: ${stats.total}`,
    `100% confidence: ${stats.atFullConfidence}`,
    `Below 100% confidence: ${stats.belowFullConfidence}`,
    `Compliant: ${stats.compliant}`,
    `Partial Compliant: ${stats.partial}`,
    `Non-Compliant: ${stats.nonCompliant}`,
    `Need attention: ${stats.attentionItems.length}`,
    '',
  ].join('\n');
}

export type FullReportExport = {
  summary: string;
  results: ParsedComplianceResult[];
  stats: ReportStats;
};

export function buildFullReportPlain({
  summary,
  results,
  stats,
}: FullReportExport): string {
  const tiers = buildTierCounts(results);
  const sections = [
    'BCP COMPLIANCE GAP ANALYSIS REPORT',
    '==================================',
    new Date().toLocaleString(),
    '',
    'COLOR CODE',
    '----------',
    `Green (${tiers.green}): 100% confidence and Compliant`,
    `Yellow (${tiers.yellow}): Partial Compliant, or confidence 70-99%`,
    `Red (${tiers.red}): Non-Compliant, or confidence below 70%`,
    '',
    buildStatsPlain(stats),
    'SUMMARY',
    '-------',
    summary.replace(/\*\*/g, ''),
    '',
    'ATTENTION FOCUS (< 100% CONFIDENCE)',
    '-----------------------------------',
  ];

  if (stats.attentionItems.length === 0) {
    sections.push('None — all points at 100% confidence.');
  } else {
    for (const item of stats.attentionItems) {
      const conf = item.confidence !== null ? `${item.confidence}%` : 'n/a';
      sections.push(
        `• ${item.title || `Point ${item.index + 1}`} — ${item.status}, confidence ${conf}`,
      );
    }
  }

  sections.push('', 'FORMATTED RESULTS', '=================', '');
  sections.push(buildFormattedResultsPlain(results));

  return sections.join('\n');
}

export function buildFullReportHtml({
  summary,
  results,
  stats,
}: FullReportExport): string {
  const tiers = buildTierCounts(results);
  const legendHtml = COLOR_LEGEND.map((entry) => {
    const count =
      entry.tier === 'green'
        ? tiers.green
        : entry.tier === 'yellow'
          ? tiers.yellow
          : tiers.red;
    const bg =
      entry.tier === 'green'
        ? '#ecfdf5'
        : entry.tier === 'yellow'
          ? '#fffbeb'
          : '#fef2f2';
    const border =
      entry.tier === 'green'
        ? '#34d399'
        : entry.tier === 'yellow'
          ? '#fbbf24'
          : '#f87171';
    return `<div style="display:inline-block;vertical-align:top;width:31%;margin:0 1% 12px 0;border:2px solid ${border};background:${bg};padding:12px;border-radius:8px;box-sizing:border-box;font-size:14px;">
      <p style="margin:0 0 4px;font-weight:bold;">${entry.label} (${count})</p>
      <p style="margin:0;font-size:12px;color:#334155;">${entry.description}</p>
    </div>`;
  }).join('');

  const summaryHtml = escapeHtml(summary)
    .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
    .replace(/^## (.+)$/gm, '<h2 style="font-size:16px;font-weight:bold;color:#4c1d95;margin:20px 0 8px;">$1</h2>')
    .replace(/^- (.+)$/gm, '<p style="margin:4px 0 4px 16px;">• $1</p>')
    .replace(/\n/g, '<br>');

  const attentionHtml =
    stats.attentionItems.length === 0
      ? '<p style="color:#047857;">All points at 100% confidence.</p>'
      : stats.attentionItems
          .map((item) => {
            const conf =
              item.confidence !== null ? `${item.confidence}%` : 'n/a';
            return `<li style="margin-bottom:8px;"><strong>${escapeHtml(item.title || `Point ${item.index + 1}`)}</strong> — ${escapeHtml(item.status)}, confidence ${conf}</li>`;
          })
          .join('');

  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <title>BCP Compliance Report</title>
</head>
<body style="font-family:Calibri,Arial,sans-serif;max-width:900px;margin:40px auto;padding:0 24px;color:#0f172a;">
  <h1 style="font-size:24px;color:#1e293b;border-bottom:2px solid #4c1d95;padding-bottom:8px;">BCP Compliance Gap Analysis Report</h1>
  <p style="font-size:12px;color:#64748b;">Generated ${escapeHtml(new Date().toLocaleString())}</p>

  <h2 style="font-size:16px;color:#334155;margin-top:24px;">Color code legend</h2>
  <div style="margin-bottom:20px;font-size:0;">${legendHtml}</div>
  <p style="font-size:12px;color:#64748b;margin-bottom:20px;">Green = 100% Compliant · Yellow = Partial or 70–99% · Red = Non-Compliant or &lt;70%</p>

  <table style="width:100%;border-collapse:collapse;margin:20px 0;font-size:13px;">
    <tr><td style="padding:6px;border:1px solid #e2e8f0;"><strong>Total points</strong></td><td style="padding:6px;border:1px solid #e2e8f0;">${stats.total}</td></tr>
    <tr><td style="padding:6px;border:1px solid #e2e8f0;"><strong>100% confidence</strong></td><td style="padding:6px;border:1px solid #e2e8f0;">${stats.atFullConfidence}</td></tr>
    <tr><td style="padding:6px;border:1px solid #e2e8f0;"><strong>Below 100%</strong></td><td style="padding:6px;border:1px solid #e2e8f0;color:#b45309;font-weight:bold;">${stats.belowFullConfidence}</td></tr>
    <tr><td style="padding:6px;border:1px solid #e2e8f0;"><strong>Need attention</strong></td><td style="padding:6px;border:1px solid #e2e8f0;color:#b45309;font-weight:bold;">${stats.attentionItems.length}</td></tr>
    <tr><td style="padding:6px;border:1px solid #e2e8f0;"><strong>Compliant / Partial / Non-Compliant</strong></td><td style="padding:6px;border:1px solid #e2e8f0;">${stats.compliant} / ${stats.partial} / ${stats.nonCompliant}</td></tr>
  </table>

  <h2 style="font-size:18px;color:#4c1d95;margin-top:28px;">Summary</h2>
  <div style="font-size:14px;line-height:1.6;background:#f8fafc;padding:16px;border-radius:8px;">${summaryHtml}</div>

  <h2 style="font-size:18px;color:#b45309;margin-top:28px;">Attention Focus (&lt; 100% confidence)</h2>
  <ul style="font-size:14px;line-height:1.6;background:#fffbeb;padding:16px 16px 16px 32px;border:1px solid #fcd34d;border-radius:8px;">${attentionHtml}</ul>

  <h2 style="font-size:18px;color:#4c1d95;margin-top:28px;">Formatted Results (${results.length})</h2>
  ${buildFormattedResultsHtml(results)}
</body>
</html>`;
}

export function buildResultsOnlyHtml(
  results: ParsedComplianceResult[],
): string {
  const tiers = buildTierCounts(results);
  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <title>BCP Formatted Results</title>
</head>
<body style="font-family:Calibri,Arial,sans-serif;max-width:900px;margin:40px auto;padding:0 24px;color:#0f172a;">
  <h1 style="font-size:22px;color:#4c1d95;">Formatted Compliance Results</h1>
  <p style="font-size:12px;color:#64748b;">Generated ${escapeHtml(new Date().toLocaleString())} · ${results.length} point(s)</p>
  <p style="font-size:12px;padding:12px;background:#f8fafc;border-radius:8px;">
    <strong>Color code:</strong>
    Green (${tiers.green}) = 100% Compliant ·
    Yellow (${tiers.yellow}) = Partial or 70–99% ·
    Red (${tiers.red}) = Non-Compliant or &lt;70%
  </p>
  ${buildFormattedResultsHtml(results)}
</body>
</html>`;
}

export function downloadTextFile(
  content: string,
  filename: string,
  mime = 'text/plain;charset=utf-8',
) {
  const blob = new Blob([content], { type: mime });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  document.body.appendChild(anchor);
  anchor.click();
  document.body.removeChild(anchor);
  URL.revokeObjectURL(url);
}

export function reportFilename(ext: string): string {
  const stamp = new Date().toISOString().slice(0, 10);
  return `bcp-compliance-report-${stamp}.${ext}`;
}

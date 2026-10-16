import { capGapsForAnalysisPoint } from './cap-gap-count';
import { normalizeGapRisk } from './doc-analysis-ready';
import { resolveAnalysisPointSeverity } from './point-compliance-status';
import type { AnalysisPoint } from './types';

/**
 * Sound when an analysis finishes (Admin > Analysis prompts). Everything here runs in the user's browser: the
 * chime is generated with the Web Audio API and the spoken summary uses the browser's own speech engine, so it
 * makes no AI call and uses no credits.
 */
export type FinishSoundMode = 'off' | 'tone' | 'voice';

export function normalizeFinishSoundMode(value?: string | null): FinishSoundMode {
  const v = (value ?? '').trim().toLowerCase();
  return v === 'tone' || v === 'voice' ? v : 'off';
}

export type AnalysisFinishSummary = {
  clauses: number;
  compliant: number;
  partial: number;
  nonCompliant: number;
  /** Clauses without a result (failed or not judged). */
  notAnalysed: number;
  gaps: number;
  highGaps: number;
  mediumGaps: number;
  lowGaps: number;
};

/** Status and gap counts of a finished run, counted the same way the gap analysis page lists them. */
export function buildAnalysisFinishSummary(points: AnalysisPoint[]): AnalysisFinishSummary {
  const summary: AnalysisFinishSummary = {
    clauses: 0,
    compliant: 0,
    partial: 0,
    nonCompliant: 0,
    notAnalysed: 0,
    gaps: 0,
    highGaps: 0,
    mediumGaps: 0,
    lowGaps: 0,
  };
  const seen = new Set<string>();
  for (const point of points) {
    const key = point.id || point.regulationPointId || '';
    if (key && seen.has(key)) continue;
    if (key) seen.add(key);
    summary.clauses++;
    const severity = resolveAnalysisPointSeverity(point);
    if (severity === 'compliant') {
      summary.compliant++;
      continue;
    }
    if (severity === 'partial_compliant') summary.partial++;
    else if (severity === 'non_compliant') summary.nonCompliant++;
    else {
      summary.notAnalysed++;
      continue;
    }
    for (const gap of capGapsForAnalysisPoint(point, true)) {
      summary.gaps++;
      const risk = normalizeGapRisk(gap.priority);
      if (risk === 'high') summary.highGaps++;
      else if (risk === 'low') summary.lowGaps++;
      else summary.mediumGaps++;
    }
  }
  return summary;
}

function plural(n: number, one: string, many: string): string {
  return `${n} ${n === 1 ? one : many}`;
}

/** Sentence read out by the browser, e.g. "Analysis finished. 5 clauses: 1 compliant, 4 partially compliant. 6 gaps:
 * 1 high risk, 2 medium risk, 3 low risk." Zero counts are left out to keep it short. */
export function analysisFinishSpeech(s: AnalysisFinishSummary): string {
  const parts = [`Analysis finished. ${plural(s.clauses, 'clause', 'clauses')}`];
  const statuses = [
    s.compliant ? `${s.compliant} compliant` : '',
    s.partial ? `${s.partial} partially compliant` : '',
    s.nonCompliant ? `${s.nonCompliant} non compliant` : '',
    s.notAnalysed ? `${s.notAnalysed} not analysed` : '',
  ].filter(Boolean);
  let text = statuses.length ? `${parts[0]}: ${statuses.join(', ')}.` : `${parts[0]}.`;
  if (!s.gaps) return `${text} No gaps found.`;
  const risks = [
    s.highGaps ? `${s.highGaps} high risk` : '',
    s.mediumGaps ? `${s.mediumGaps} medium risk` : '',
    s.lowGaps ? `${s.lowGaps} low risk` : '',
  ].filter(Boolean);
  text += ` ${plural(s.gaps, 'gap', 'gaps')}: ${risks.join(', ')}.`;
  return text;
}

/** Short two-note chime. Resolves when it has finished (or at once when the browser cannot play sound). */
export function playFinishChime(): Promise<void> {
  try {
    const Ctx =
      (globalThis as { AudioContext?: typeof AudioContext; webkitAudioContext?: typeof AudioContext }).AudioContext ??
      (globalThis as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctx) return Promise.resolve();
    const ctx = new Ctx();
    void ctx.resume?.();
    const notes = [
      { freq: 880, at: 0 },
      { freq: 1318.5, at: 0.18 },
    ];
    for (const note of notes) {
      const osc = ctx.createOscillator();
      const gain = ctx.createGain();
      osc.type = 'sine';
      osc.frequency.value = note.freq;
      const start = ctx.currentTime + note.at;
      gain.gain.setValueAtTime(0.0001, start);
      gain.gain.exponentialRampToValueAtTime(0.25, start + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.0001, start + 0.45);
      osc.connect(gain).connect(ctx.destination);
      osc.start(start);
      osc.stop(start + 0.5);
    }
    return new Promise((resolve) =>
      setTimeout(() => {
        void ctx.close?.();
        resolve();
      }, 750),
    );
  } catch {
    return Promise.resolve();
  }
}

/** Reads the text with the browser's own voice (no AI). Does nothing where speech is not available. */
export function speakText(text: string): void {
  try {
    const synth = (globalThis as { speechSynthesis?: SpeechSynthesis }).speechSynthesis;
    const Utterance = (globalThis as { SpeechSynthesisUtterance?: typeof SpeechSynthesisUtterance })
      .SpeechSynthesisUtterance;
    if (!synth || !Utterance) return;
    synth.cancel();
    const utterance = new Utterance(text);
    utterance.lang = 'en-US';
    utterance.rate = 1;
    synth.speak(utterance);
  } catch {
    // Sound is a convenience; never break the page over it.
  }
}

/** Plays what the mode asks for: nothing, the chime, or the chime and then the spoken summary. */
export async function notifyAnalysisFinished(mode: FinishSoundMode, points: AnalysisPoint[]): Promise<void> {
  if (mode === 'off') return;
  await playFinishChime();
  if (mode === 'voice') speakText(analysisFinishSpeech(buildAnalysisFinishSummary(points)));
}

/** Failed run: chime, and in voice mode a short spoken note. */
export async function notifyAnalysisFailed(mode: FinishSoundMode): Promise<void> {
  if (mode === 'off') return;
  await playFinishChime();
  if (mode === 'voice') speakText('Analysis stopped with an error. Please open the run to see what failed.');
}

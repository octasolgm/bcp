import clause32Fixture from './fixtures/cbuae-clause-3.2.json';
import type { DemoTemplatePointLike } from './demo-template-point-preview';
import { parseRegulElementCapSegments, parseRegulElementGapSegments } from './regul-fields';
import { parseRegulElementCapGaps } from './cap-gap-count';
import { parseReferenceComplianceBlock } from '../ai-lab/parse-reference-response';
import { formatDemoJudgmentLandingMessage } from './demo-template-point-preview';

const CLAUSE_32_INTERPRETATION = (clause32Fixture.interpretation as string).trim();

const CLAUSE_32_ROW: DemoTemplatePointLike = {
  id: 'fixture-3.2',
  clauseNo: '3.2',
  clauseTitle: 'Confidentiality and Data Protection',
  designStatus: 'partial',
  operatingStatus: 'partial',
  overallStatus: 'partial',
  confidence: 0.72,
  interpretation: CLAUSE_32_INTERPRETATION,
  policyExtract: [],
  documentReference: 'Internal AML Manual 290626.pdf, pp.14, 38-42',
  gapDescription: '',
  suggestedAction: '',
  gapDirection: '',
};

function segmentsPreserveText(segments: string[], full: string): boolean {
  for (const seg of segments) {
    if (!full.includes(seg.trim())) return false;
  }
  return true;
}

describe('regul gap JSON parity', () => {
  it('clause 3.2 gap analysis field matches seed interpretation', () => {
    const landing = formatDemoJudgmentLandingMessage(CLAUSE_32_ROW);
    const block = parseReferenceComplianceBlock(landing);
    expect(block.gapAnalysis.trim()).toBe(CLAUSE_32_INTERPRETATION);
  });

  it('clause 3.2 CAP uses full interpretation (preamble + all 5 elements)', () => {
    const segments = parseRegulElementCapSegments(CLAUSE_32_INTERPRETATION);
    const capGaps = parseRegulElementCapGaps(CLAUSE_32_INTERPRETATION);

    expect(segments.length).toBeGreaterThanOrEqual(6);
    expect(segments[0].toLowerCase().startsWith('the regulator')).toBe(true);
    expect(segmentsPreserveText(segments, CLAUSE_32_INTERPRETATION)).toBe(true);
    expect(capGaps.length).toBe(segments.length);
    expect(capGaps.map((g) => g.missing)).toEqual(segments);
    expect(parseRegulElementGapSegments(CLAUSE_32_INTERPRETATION).length).toBe(3);
  });

  it('clause 3.4-c detects "Not clearly covered" element gap', () => {
    const interpretation =
      "The regulator requires FIs to have an explicit, absolute prohibition on any dealings with Shell Banks.\n\n" +
      "Element 1 (correspondent accounts): Covered - manual prohibits shell bank correspondent accounts. " +
      "Element 2 (accepting funds FROM shell banks): Not clearly covered - no explicit standalone prohibition on accepting deposits directly from a shell bank.";
    expect(parseRegulElementGapSegments(interpretation).length).toBe(1);
    expect(parseRegulElementCapGaps(interpretation).length).toBeGreaterThanOrEqual(2);
  });

  it('clause 3.4-e detects prose "No provision found" gap', () => {
    const interpretation =
      "The regulator expects the Financial Institution to have an explicit policy prohibiting bearer shares.\n\n" +
      "No provision found in the reviewed excerpts of the Internal AML Manual addressing bearer shares or bearer share warrants at all.";
    const gaps = parseRegulElementGapSegments(interpretation);
    expect(gaps.length).toBe(1);
    expect(gaps[0].toLowerCase()).toContain('no provision found');
  });
});

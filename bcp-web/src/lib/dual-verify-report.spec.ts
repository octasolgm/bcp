import {
  buildReportSummary,
  exportableReportItems,
  mergeReportItems,
  removeReportItemFromBag,
  reportItemsToSortedArray,
  type DualVerifyReportItem,
} from './dual-verify-report';

describe('dual-verify-report', () => {
  it('merges in-flight status over previously loaded completed items', () => {
    const existing = new Map<string, DualVerifyReportItem>([
      [
        '3.1.1',
        {
          pointId: '3.1.1',
          status: 'loaded',
          landingMessage: 'a',
          llmMessage: 'b',
        },
      ],
    ]);
    const incoming: DualVerifyReportItem[] = [
      { pointId: '3.1.1', status: 'running' },
    ];
    const merged = mergeReportItems(existing, incoming);
    expect(merged.get('3.1.1')?.status).toBe('running');
    expect(merged.get('3.1.1')?.landingMessage).toBe('a');
  });

  it('merges completed items over in-progress ones', () => {
    const existing = new Map<string, DualVerifyReportItem>([
      [
        '2.1.1',
        {
          pointId: '2.1.1',
          status: 'running',
          landingMessage: 'a',
        },
      ],
    ]);
    const incoming: DualVerifyReportItem[] = [
      {
        pointId: '2.1.1',
        status: 'completed',
        landingMessage: 'a',
        llmMessage: 'b',
        agreement: {
          status: 'aligned',
          label: 'Aligned',
          landingStatus: 'Compliant',
          llmStatus: 'Compliant',
          landingConfidence: 90,
          llmConfidence: 90,
          confidenceDelta: 0,
          summary: 'ok',
        },
      },
    ];
    const merged = mergeReportItems(existing, incoming);
    expect(merged.get('2.1.1')?.status).toBe('completed');
    expect(merged.get('2.1.1')?.llmMessage).toBe('b');
  });

  it('sorts report items numerically', () => {
    const map = new Map<string, DualVerifyReportItem>([
      ['2.1.2', { pointId: '2.1.2', status: 'loaded' }],
      ['2.1.1', { pointId: '2.1.1', status: 'loaded' }],
    ]);
    expect(reportItemsToSortedArray(map).map((i) => i.pointId)).toEqual(['2.1.1', '2.1.2']);
  });

  it('summarizes aligned vs review counts', () => {
    const summary = buildReportSummary([
      {
        pointId: '2.1.1',
        status: 'completed',
        landingMessage: 'a',
        llmMessage: 'b',
        agreement: {
          status: 'aligned',
          label: 'Aligned',
          landingStatus: 'Compliant',
          llmStatus: 'Compliant',
          landingConfidence: 90,
          llmConfidence: 90,
          confidenceDelta: 0,
          summary: 'ok',
        },
      },
      {
        pointId: '2.1.2',
        status: 'completed',
        landingMessage: 'a',
        llmMessage: 'b',
        agreement: {
          status: 'status_mismatch',
          label: 'Mismatch',
          landingStatus: 'Compliant',
          llmStatus: 'Non-Compliant',
          landingConfidence: 90,
          llmConfidence: 40,
          confidenceDelta: 50,
          summary: 'gap',
        },
      },
    ]);
    expect(summary.completed).toBe(2);
    expect(summary.aligned).toBe(1);
    expect(summary.needsReview).toBe(1);
  });

  it('exportableReportItems requires both pass messages', () => {
    const items: DualVerifyReportItem[] = [
      { pointId: '2.1.1', status: 'completed', landingMessage: 'a', llmMessage: 'b' },
      { pointId: '3.1.1', status: 'completed', landingMessage: 'a' },
    ];
    expect(exportableReportItems(items).length).toBe(1);
  });

  it('removeReportItemFromBag deletes one point', () => {
    const bag = new Map<string, DualVerifyReportItem>([
      ['2.1.1', { pointId: '2.1.1', status: 'loaded' }],
      ['2.1.2', { pointId: '2.1.2', status: 'loaded' }],
    ]);
    const next = removeReportItemFromBag(bag, '2.1.1');
    expect(next.size).toBe(1);
    expect(next.has('2.1.1')).toBe(false);
    expect(next.get('2.1.2')?.pointId).toBe('2.1.2');
    expect(bag.size).toBe(2);
  });
});

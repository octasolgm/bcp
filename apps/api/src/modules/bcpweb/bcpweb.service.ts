import { Injectable, NotFoundException } from '@nestjs/common';
import { randomUUID } from 'crypto';
import {
  BcpwebAnalysisService,
  type StoredSessionFiles,
} from './bcpweb-analysis.service';
import type {
  BcpwebAnalysisSession,
  BcpwebComplianceItem,
  BcpwebDashboardMetrics,
  BcpwebDocument,
  BcpwebRegulation,
  BcpwebSeverity,
  BcpwebUpdateItemDto,
} from './bcpweb.types';
import {
  BRANCHES,
  DEMO_ITEMS,
  DEMO_SESSION,
  DEMO_SESSION_ID,
  DOCUMENTS,
  REGULATIONS,
  buildComplianceItems,
  buildDashboardMetrics,
  categoryCounts,
} from './data/bcpweb.seed';

interface SessionBundle {
  session: BcpwebAnalysisSession;
  items: BcpwebComplianceItem[];
  files?: StoredSessionFiles;
}

/**
 * In-memory BCP Web store — isolated from existing BCP modules.
 */
@Injectable()
export class BcpwebService {
  private store = new Map<string, SessionBundle>();

  constructor(private readonly analysisEngine: BcpwebAnalysisService) {
    this.store.set(DEMO_SESSION_ID, {
      session: { ...DEMO_SESSION },
      items: DEMO_ITEMS.map((i) => ({ ...i })),
    });
  }

  getDashboard(): BcpwebDashboardMetrics {
    return buildDashboardMetrics();
  }

  getBranches() {
    return BRANCHES;
  }

  getRegulations(category?: string): {
    items: BcpwebRegulation[];
    counts: Record<string, number>;
  } {
    const counts = categoryCounts();
    if (!category || category === 'all') {
      return { items: REGULATIONS, counts };
    }
    return {
      items: REGULATIONS.filter((r) => r.category === category),
      counts,
    };
  }

  getDocuments(category?: string): BcpwebDocument[] {
    if (!category || category === 'all') return DOCUMENTS;
    const map: Record<string, string[]> = {
      'AML/CFT': ['AML/CFT', 'AML/Sanctions'],
      Sanctions: ['Sanctions', 'AML/Sanctions'],
      'KYC/CDD': ['KYC/CDD'],
    };
    const cats = map[category] ?? [category];
    return DOCUMENTS.filter((d) => cats.some((c) => d.category.includes(c)));
  }

  getDemoSession(): BcpwebAnalysisSession {
    return this.getSession(DEMO_SESSION_ID);
  }

  getSession(id: string): BcpwebAnalysisSession {
    const bundle = this.store.get(id);
    if (!bundle) throw new NotFoundException('Analysis session not found');
    return bundle.session;
  }

  getSessionItems(sessionId: string): BcpwebComplianceItem[] {
    const bundle = this.store.get(sessionId);
    if (!bundle) throw new NotFoundException('Session items not found');
    return bundle.items;
  }

  /** Create session with uploaded PDFs and run Gemini analysis */
  async createSessionWithFiles(
    regulationId: string,
    files: StoredSessionFiles,
  ): Promise<BcpwebAnalysisSession> {
    const regulation = REGULATIONS.find((r) => r.id === regulationId);
    const id = randomUUID();
    const session: BcpwebAnalysisSession = {
      id,
      branchId: 'branch-1',
      regulationId,
      regulationTitle: regulation?.title ?? 'Regulation',
      internalDocName: files.internalFileName,
      regulationDocName: files.regulationFileName,
      status: 'processing',
      progressPct: 0,
      progressStep: 'parsing',
      briefing: '',
      totalItems: 0,
      criticalCount: 0,
      highCount: 0,
      mediumCount: 0,
      lowCount: 0,
      compliantCount: 0,
      analysisDate: new Date().toLocaleDateString('en-GB', {
        day: '2-digit',
        month: 'short',
        year: 'numeric',
      }),
      createdAt: new Date().toISOString(),
      completedAt: null,
    };

    this.store.set(id, { session, items: [], files });
    void this.runRealAnalysis(id, regulationId, files);
    return session;
  }

  /** Legacy JSON-only create (no files → demo seed) */
  async createSession(body: {
    regulationId: string;
    internalDocName: string;
    regulationDocName: string;
  }): Promise<BcpwebAnalysisSession> {
    return this.createSessionWithFiles(body.regulationId, {
      internalBuffer: Buffer.alloc(0),
      regulationBuffer: Buffer.alloc(0),
      internalFileName: body.internalDocName,
      regulationFileName: body.regulationDocName,
    });
  }

  getProgress(sessionId: string): BcpwebAnalysisSession {
    return this.getSession(sessionId);
  }

  updateItem(
    sessionId: string,
    itemId: string,
    dto: BcpwebUpdateItemDto,
  ): BcpwebComplianceItem {
    const bundle = this.store.get(sessionId);
    if (!bundle) throw new NotFoundException('Session not found');
    const idx = bundle.items.findIndex((i) => i.id === itemId);
    if (idx < 0) throw new NotFoundException('Item not found');
    bundle.items[idx] = { ...bundle.items[idx], ...dto };
    return bundle.items[idx];
  }

  signOffItem(sessionId: string, itemId: string): BcpwebComplianceItem {
    const bundle = this.store.get(sessionId);
    if (!bundle) throw new NotFoundException('Session not found');
    const idx = bundle.items.findIndex((i) => i.id === itemId);
    if (idx < 0) throw new NotFoundException('Item not found');
    bundle.items[idx] = {
      ...bundle.items[idx],
      signedOff: true,
      signedOffAt: new Date().toISOString(),
    };
    return bundle.items[idx];
  }

  getPdfPage(
    sessionId: string,
    source: 'regulation' | 'policy',
    page: number,
    itemId?: string,
  ): { extractedText: string; totalPages: number; title: string } {
    const bundle = this.store.get(sessionId);
    if (!bundle) throw new NotFoundException('Session not found');

    const item =
      (itemId ? bundle.items.find((i) => i.id === itemId) : undefined) ??
      bundle.items.find((i) =>
        source === 'regulation'
          ? i.regulatoryPdfPage === page
          : i.policyPdfPage === page,
      ) ??
      bundle.items[0];

    const text =
      source === 'regulation' ? item?.regulatoryText : item?.policyText;
    const sectionRef = item?.sectionRef ?? '2.1';

    return {
      extractedText: text ?? 'No extracted text for this page.',
      totalPages: source === 'regulation' ? 23 : 41,
      title:
        source === 'regulation'
          ? `Regulation — §${sectionRef}`
          : `Policy — §${sectionRef}`,
    };
  }

  private async runRealAnalysis(
    sessionId: string,
    regulationId: string,
    files: StoredSessionFiles,
  ): Promise<void> {
    const bundle = this.store.get(sessionId);
    if (!bundle) return;

    const hasFiles =
      files.internalBuffer.length > 0 && files.regulationBuffer.length > 0;

    try {
      const items = hasFiles
        ? await this.analysisEngine.runAnalysis(
            sessionId,
            regulationId,
            files,
            (update) => {
              const s = bundle.session;
              if (update.progressPct != null) s.progressPct = update.progressPct;
              if (update.progressStep) s.progressStep = update.progressStep;
              if (update.briefingAppend) {
                s.briefing = (s.briefing || '') + update.briefingAppend;
              }
              this.store.set(sessionId, { ...bundle, session: { ...s } });
            },
          )
        : buildComplianceItems(sessionId);

      bundle.items = items;
      this.finalizeSession(bundle, items);
      this.store.set(sessionId, bundle);
    } catch {
      bundle.items = buildComplianceItems(sessionId);
      bundle.session.status = 'failed';
      this.store.set(sessionId, bundle);
    }
  }

  private finalizeSession(
    bundle: SessionBundle,
    items: BcpwebComplianceItem[],
  ): void {
    const count = (s: BcpwebSeverity) =>
      items.filter((i) => i.severity === s).length;

    bundle.session = {
      ...bundle.session,
      status: 'completed',
      progressPct: 100,
      progressStep: 'complete',
      totalItems: items.length,
      criticalCount: count('critical'),
      highCount: count('high'),
      mediumCount: count('medium'),
      lowCount: count('low'),
      compliantCount: count('compliant'),
      completedAt: new Date().toISOString(),
    };
    if (!bundle.session.briefing) {
      bundle.session.briefing = `BRIEFING: Gap Analysis complete. ${items.length} findings identified.`;
    }
  }
}

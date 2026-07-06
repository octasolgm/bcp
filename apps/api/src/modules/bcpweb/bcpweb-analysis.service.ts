import { Injectable, Logger } from '@nestjs/common';
import { ConfigService } from '@nestjs/config';
import { randomUUID } from 'crypto';
import { BcpAnalyzeService } from '../ai/services/bcp-analyze.service';
import type { UploadedPdfFile } from '../ai/types/ai-response.types';
import { LandingAiService } from '../landing-ai/services/landing-ai.service';
import type { GovRequirementPoint } from '../landing-ai/types/landing-ai.types';
import { filterComparableGovPoints } from '../landing-ai/utils/gov-point-filter';
import type {
  BcpwebAnalysisSession,
  BcpwebComplianceItem,
  BcpwebSeverity,
} from './bcpweb.types';
import {
  buildBcpwebGapItemPrompt,
  buildExtractGovPointsPrompt,
  type GeminiGapItemJson,
  type GeminiGovPointJson,
  parseGeminiJson,
} from './bcpweb-gap-prompt';
import { buildComplianceItems, REGULATIONS } from './data/bcpweb.seed';

export interface AnalysisProgressCallback {
  (update: Partial<BcpwebAnalysisSession> & { briefingAppend?: string }): void;
}

export interface StoredSessionFiles {
  internalBuffer: Buffer;
  regulationBuffer: Buffer;
  internalFileName: string;
  regulationFileName: string;
}

const MAX_POINTS = 12;

/**
 * Real gap analysis: Landing AI extract (optional) + Gemini per-point compare.
 */
@Injectable()
export class BcpwebAnalysisService {
  private readonly logger = new Logger(BcpwebAnalysisService.name);

  constructor(
    private readonly bcpAnalyze: BcpAnalyzeService,
    private readonly landingAi: LandingAiService,
    private readonly config: ConfigService,
  ) {}

  hasGemini(): boolean {
    const key = this.config.get<string>('GEMINI_API_KEY');
    return Boolean(key?.trim() && key !== 'your-gemini-key');
  }

  /** Run full analysis pipeline */
  async runAnalysis(
    sessionId: string,
    regulationId: string,
    files: StoredSessionFiles,
    onProgress: AnalysisProgressCallback,
  ): Promise<BcpwebComplianceItem[]> {
    const regulation = REGULATIONS.find((r) => r.id === regulationId);
    const regulationTitle = regulation?.title ?? 'Regulation';

    onProgress({
      progressPct: 5,
      progressStep: 'parsing',
      briefingAppend:
        'BRIEFING: TFS Compliance Gap Analysis\n\nParsing and chunking documents…',
    });

    const internalPdf: UploadedPdfFile = {
      originalname: files.internalFileName,
      buffer: files.internalBuffer,
      size: files.internalBuffer.length,
      mimetype: 'application/pdf',
    };
    const regulationPdf: UploadedPdfFile = {
      originalname: files.regulationFileName,
      buffer: files.regulationBuffer,
      size: files.regulationBuffer.length,
      mimetype: 'application/pdf',
    };

    if (!this.hasGemini()) {
      this.logger.warn('GEMINI_API_KEY missing — using demo seed data');
      await this.simulateDelay(2000);
      onProgress({
        progressPct: 100,
        progressStep: 'complete',
        briefingAppend: '\n\n(Demo mode — configure GEMINI_API_KEY for live analysis)',
      });
      return buildComplianceItems(sessionId);
    }

    const govPoints = await this.extractGovPoints(
      regulationPdf,
      regulationTitle,
      onProgress,
    );

    onProgress({
      progressPct: 45,
      progressStep: 'cross',
      briefingAppend: `\n\nLoading regulation clauses (${govPoints.length} found)…\nCross-referencing requirements…`,
    });

    const items: BcpwebComplianceItem[] = [];
    const total = Math.min(govPoints.length, MAX_POINTS);

    for (let i = 0; i < total; i++) {
      const point = govPoints[i];
      const pct = 45 + Math.round(((i + 1) / total) * 50);
      onProgress({
        progressPct: Math.min(pct, 95),
        progressStep: i < total - 1 ? 'gaps' : 'actions',
        briefingAppend:
          i === 0 ? '\n\nIdentifying gaps and risk levels…' : undefined,
      });

      try {
        const item = await this.analyzePointWithGemini(
          sessionId,
          point,
          regulationTitle,
          files.internalFileName,
          internalPdf,
          regulationPdf,
          i + 1,
        );
        items.push(item);
      } catch (err) {
        this.logger.error(`Point ${point.point_id} failed: ${err}`);
      }

      await this.simulateDelay(400);
    }

    onProgress({
      progressPct: 100,
      progressStep: 'complete',
      briefingAppend: `\n\nAnalysis complete. ${items.length} findings identified.`,
    });

    return items.length > 0 ? items : buildComplianceItems(sessionId);
  }

  private async extractGovPoints(
    regulationPdf: UploadedPdfFile,
    regulationTitle: string,
    onProgress: AnalysisProgressCallback,
  ): Promise<GovRequirementPoint[]> {
    onProgress({ progressPct: 20, progressStep: 'clauses' });

    try {
      const status = await this.landingAi.getStatus();
      if (status.configured) {
        const extracted = await this.landingAi.extractPoints(
          regulationPdf.buffer,
          regulationPdf.originalname,
          'gov_requirement_points',
        );
        const filtered = filterComparableGovPoints(extracted.points);
        if (filtered.comparable.length > 0) {
          this.logger.log(`Landing AI extracted ${filtered.comparable.length} gov points`);
          return filtered.comparable.slice(0, MAX_POINTS);
        }
      }
    } catch (err) {
      this.logger.warn(`Landing AI extract failed, falling back to Gemini: ${err}`);
    }

    const prompt = buildExtractGovPointsPrompt(regulationTitle);
    const result = await this.bcpAnalyze.analyze([regulationPdf], prompt);
    const message =
      typeof result.message === 'string' ? result.message : JSON.stringify(result.message);
    const parsed = parseGeminiJson<GeminiGovPointJson[]>(message);
    if (parsed?.length) {
      return parsed.map((p) => ({
        point_id: p.point_id,
        title: p.title,
        text: p.text,
        section: p.section,
        page_hint: p.page_hint,
        point_type: 'mandatory' as const,
      }));
    }

    this.logger.warn('Gov extract failed — using default TFS point list');
    return this.defaultTfsPoints();
  }

  private async analyzePointWithGemini(
    sessionId: string,
    point: GovRequirementPoint,
    regulationTitle: string,
    internalDocName: string,
    internalPdf: UploadedPdfFile,
    regulationPdf: UploadedPdfFile,
    serialNo: number,
  ): Promise<BcpwebComplianceItem> {
    const prompt = buildBcpwebGapItemPrompt(point, regulationTitle, internalDocName);
    const result = await this.bcpAnalyze.analyze(
      [internalPdf, regulationPdf],
      prompt,
      this.config.get<string>('GEMINI_DEFAULT_MODEL') ?? 'gemini-2.5-flash-lite',
    );

    const message =
      typeof result.message === 'string' ? result.message : JSON.stringify(result.message);
    const parsed = parseGeminiJson<GeminiGapItemJson>(message);

    const severity = this.normalizeSeverity(parsed?.severity, parsed?.gapsIdentified);
    const clauseNo = parsed?.clauseNo ?? `§${point.point_id}`;
    const sectionRef = parsed?.sectionRef ?? point.point_id.replace(/^§/, '');

    return {
      id: randomUUID(),
      sessionId,
      serialNo,
      clauseNo,
      sectionRef,
      title: parsed?.title ?? point.title ?? `Requirement ${point.point_id}`,
      severity,
      regulatoryText: parsed?.regulatoryText ?? point.text,
      regulatoryPdfPage: parsed?.regulatoryPdfPage ?? point.page_hint ?? 1,
      policyText: parsed?.policyText ?? 'Pending review.',
      policyPdfPage: parsed?.policyPdfPage ?? 1,
      gapsIdentified: parsed?.gapsIdentified ?? '',
      managementResponse: '',
      designEffectiveness: '',
      operatingEffectiveness: '',
      overallEffectiveness: '',
      documentReference: '',
      evidenceImplementation: '',
      evidenceReference: '',
      responsibleDepartment: '',
      complianceStatus: '',
      targetDate: '',
      conclusion: parsed?.conclusion ?? '',
      observation: parsed?.observation ?? '',
      actionPlan: parsed?.actionPlan ?? '',
      assignedTo: '',
      signedOff: false,
      signedOffAt: null,
    };
  }

  private normalizeSeverity(raw?: string, gaps?: string): BcpwebSeverity {
    const s = (raw ?? '').toLowerCase();
    if (
      (s.includes('compliant') || s === 'compliant') &&
      !s.includes('non') &&
      !s.includes('partial')
    ) {
      return 'compliant';
    }
    if (s === 'critical' || s.includes('critical')) return 'critical';
    if (s === 'high' || s.includes('non')) return 'high';
    if (s === 'medium' || s.includes('partial')) return 'medium';
    if (s === 'low') return 'low';
    if (gaps?.toLowerCase().includes('no gap')) return 'compliant';
    return 'medium';
  }

  private defaultTfsPoints(): GovRequirementPoint[] {
    return [
      { point_id: '2.1', title: 'Senior Management SCP Approval and Oversight', text: 'Senior management commitment to the LFI SCP must be demonstrated with annual review and approval.', point_type: 'mandatory', page_hint: 6 },
      { point_id: '2.3', title: 'Sanctions Risk Appetite Written Documentation', text: 'The LFI must maintain a comprehensive written sanctions compliance programme including documented risk appetite.', point_type: 'mandatory', page_hint: 7 },
      { point_id: '3.7', title: 'Confirmed Match Freeze Without Delay 24 Hours', text: 'LFIs must freeze without delay within 24 hours upon confirmed match per Cabinet Decision 74.', point_type: 'mandatory', page_hint: 12 },
      { point_id: '4', title: 'Notification to CBUAE and Executive Office Timing', text: 'Report TFS measures to CBUAE and Executive Office within required timelines.', point_type: 'mandatory', page_hint: 14 },
      { point_id: '2.7', title: 'Independent Audit Testing Processes and Systems', text: 'Independent audit must assess SCP effectiveness at least annually.', point_type: 'mandatory', page_hint: 8 },
      { point_id: '2.8', title: 'Statutory Record Retention Period', text: 'Records retained at least five years per AML-CFT Law.', point_type: 'mandatory', page_hint: 9 },
      { point_id: '3.3', title: 'Customer Screening Lifecycle Triggers', text: 'Screening at onboarding, periodic review, and trigger events.', point_type: 'mandatory', page_hint: 11 },
      { point_id: '3.5', title: 'White List Management and False Positive Documentation', text: 'White lists must be periodically reviewed and updated.', point_type: 'mandatory', page_hint: 14 },
      { point_id: '3.6', title: 'Payments Screening Information Fields', text: 'Screen counterparty information on incoming and outgoing payments.', point_type: 'mandatory', page_hint: 15 },
    ];
  }

  private simulateDelay(ms: number): Promise<void> {
    return new Promise((r) => setTimeout(r, ms));
  }
}

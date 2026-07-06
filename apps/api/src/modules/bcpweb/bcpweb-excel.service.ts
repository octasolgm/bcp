import { Injectable, NotFoundException } from '@nestjs/common';
import * as ExcelJS from 'exceljs';
import type { BcpwebComplianceItem, BcpwebAnalysisSession } from './bcpweb.types';
import { BcpwebService } from './bcpweb.service';

/**
 * Excel export for BCP Web gap analysis working document.
 */
@Injectable()
export class BcpwebExcelService {
  constructor(private readonly bcpwebService: BcpwebService) {}

  /** Build Gap_Analysis_Working.xlsx buffer */
  async buildExport(sessionId: string): Promise<Buffer> {
    const session = this.bcpwebService.getSession(sessionId);
    let items: BcpwebComplianceItem[];
    try {
      items = this.bcpwebService.getSessionItems(sessionId);
    } catch {
      throw new NotFoundException('Session items not found');
    }

    const workbook = new ExcelJS.Workbook();
    this.addCoverSheet(workbook, session);
    this.addAmlGuidelinesSheet(workbook, items);

    const buffer = await workbook.xlsx.writeBuffer();
    return Buffer.from(buffer);
  }

  private addCoverSheet(workbook: ExcelJS.Workbook, session: BcpwebAnalysisSession): void {
    const sheet = workbook.addWorksheet('Cover');
    sheet.getCell('A1').value = 'INTERNAL AUDIT GROUP';
    sheet.getCell('A3').value = 'Audit Title:';
    sheet.getCell('B3').value = `Gap Analysis — ${session.internalDocName}`;
    sheet.getCell('A4').value = 'Document Reviewed:';
    sheet.getCell('B4').value = session.internalDocName;
    sheet.getCell('A5').value = 'Benchmark Regulation:';
    sheet.getCell('B5').value = session.regulationDocName;
    sheet.getCell('A6').value = 'Analysis Date:';
    sheet.getCell('B6').value = session.analysisDate;
    sheet.getCell('A7').value = 'Prepared by:';
    sheet.getCell('B7').value =
      'Reguliq Platform — AI-assisted, compliance officer reviewed';
    sheet.getCell('A8').value = 'Version:';
    sheet.getCell('B8').value = 'v1.0';
    sheet.getCell('A10').value =
      'NOTE: Columns D, O, P, Q are AI-drafted and require compliance officer review and sign-off before this document is considered final.';
    sheet.getCell('A11').value =
      'Columns F, G, H, I, K, L, M, N, R must be completed by the responsible compliance officer.';
  }

  private addAmlGuidelinesSheet(
    workbook: ExcelJS.Workbook,
    items: BcpwebComplianceItem[],
  ): void {
    const sheet = workbook.addWorksheet('AML Guidelines');
    const headers = [
      'Serial #',
      'Clause No.',
      'Rules by CB UAE',
      'Interpretation and expected action to be done to Comply to this requirement',
      'Actions Taken by Management',
      'Design Effectiveness',
      'Operate Effectiveness',
      'Both ( D & OE)',
      'Document Reference',
      'Policy Extract',
      'Actions Taken to Implement the Regulatory Requirement as per the appro',
      'Evidence Reference',
      'Responsible Dept.',
      'Compliance Status',
      'Conclusion',
      'Observation',
      'Action Plans',
      'Target Date',
      'Assigned To',
      'Signed Off',
      'IA Comments, if any',
    ];
    sheet.addRow(headers);
    const headerRow = sheet.getRow(1);
    headerRow.font = { bold: true };

    for (const item of items) {
      sheet.addRow([
        item.serialNo,
        item.clauseNo,
        item.regulatoryText,
        item.gapsIdentified,
        item.managementResponse,
        item.designEffectiveness,
        item.operatingEffectiveness,
        item.overallEffectiveness,
        item.documentReference,
        item.policyText,
        item.evidenceImplementation,
        item.evidenceReference,
        item.responsibleDepartment,
        item.complianceStatus,
        item.conclusion,
        item.observation,
        item.actionPlan,
        item.targetDate,
        item.assignedTo,
        item.signedOff ? 'Yes' : 'Pending',
        '',
      ]);
    }

    sheet.columns.forEach((col) => {
      col.width = 24;
    });
  }
}

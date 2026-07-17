import { Component, Input, OnChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import {
  hasDisplayableFulfilledClauses,
  parseBulletLines,
  parseCapGaps,
  parseReferenceCitation,
  parseReferenceComplianceBlock,
  referenceBlockBadgeLabel,
  referenceBlockToTier,
  requirementDisplayLines,
  type ReferenceComplianceBlock,
} from '../../../lib/ai-lab/parse-reference-response';
import type { ColorTier } from '../../../lib/ai-lab/color-tier';

@Component({
  selector: 'app-reference-compliance-card',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './reference-compliance-card.component.html',
  styleUrl: './reference-compliance-card.component.scss',
})
export class ReferenceComplianceCardComponent implements OnChanges {
  @Input({ required: true }) message!: string;
  @Input() capOnly = false;

  block!: ReferenceComplianceBlock;
  tier: ColorTier = 'neutral';
  badge = '';
  reqLines: string[] = [];
  citation: { page: string | null; section: string | null; quote: string | null } = {
    page: null,
    section: null,
    quote: null,
  };
  isMissing = false;
  fulfilledLines: string[] = [];
  showFulfilled = false;
  capGaps: ReturnType<typeof parseCapGaps> = [];

  ngOnChanges(): void {
    this.block = parseReferenceComplianceBlock(this.message?.trim() ?? '');
    this.tier = referenceBlockToTier(this.block);
    this.badge = referenceBlockBadgeLabel(this.block);
    this.reqLines = requirementDisplayLines(this.block.body);
    this.citation = parseReferenceCitation(this.block.outputResponse);
    this.isMissing = /no corresponding procedure found/i.test(this.block.outputResponse);
    this.showFulfilled = hasDisplayableFulfilledClauses(this.block.fulfilledClauses);
    this.fulfilledLines = parseBulletLines(this.block.fulfilledClauses ?? '');
    const cap = this.block.correctiveAction?.trim() ?? '';
    this.capGaps = cap && cap !== 'N/A' ? parseCapGaps(cap) : [];
  }

  tierClass(prefix: string): string {
    return `${prefix}-${this.tier}`;
  }

  stripLinePrefix(line: string): string {
    return line.replace(/^\d+[.)]\s*/, '');
  }
}

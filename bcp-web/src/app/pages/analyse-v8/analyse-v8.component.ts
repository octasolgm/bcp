import { Component, ElementRef, OnDestroy, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { InProgressNavButtonComponent } from '../../components/in-progress-nav-button/in-progress-nav-button.component';
import { DualVerifyResultCardComponent } from '../../components/dual-verify-result-card/dual-verify-result-card.component';
import { AnalyseBase } from '../shared/analyse-base';
import { startPanelResize, type PanelResizeKind } from '../shared/panel-resize';

@Component({
  selector: 'app-analyse-v8',
  standalone: true,
  imports: [CommonModule, FormsModule, InProgressNavButtonComponent, DualVerifyResultCardComponent],
  templateUrl: './analyse-v8.component.html',
  styleUrl: './analyse-v8.component.scss',
})
export class AnalyseV8Component extends AnalyseBase implements OnDestroy {
  readonly versionLabel = 'V8 — Points on Top';
  readonly versionPath = '/analyse-v8';

  @ViewChild('workspaceEl') workspaceEl?: ElementRef<HTMLElement>;

  setupRegPct = 28;
  setupCompliancePct = 28;
  colLeftWidth = 300;
  colMidWidth = 280;

  startTopSplit(which: 'reg' | 'compliance', event: MouseEvent): void {
    const container = (event.target as HTMLElement).closest('.setup-row-inner');
    const startVal = which === 'reg' ? this.setupRegPct : this.setupCompliancePct;
    startPanelResize(
      {
        kind: 'setup-split',
        startX: event.clientX,
        startY: event.clientY,
        startVal,
        containerWidth: container?.clientWidth ?? 0,
      },
      event,
      (_kind, value) => {
        if (which === 'reg') {
          this.setupRegPct = Math.min(value, 72 - this.setupCompliancePct);
        } else {
          this.setupCompliancePct = Math.min(value, 72 - this.setupRegPct);
        }
      },
      { 'setup-split': { min: 18, max: 45 } },
    );
  }

  startColResize(side: 'left' | 'right', event: MouseEvent): void {
    const kind: PanelResizeKind = side === 'left' ? 'col-left' : 'col-right';
    startPanelResize(
      {
        kind,
        startX: event.clientX,
        startY: event.clientY,
        startVal: side === 'left' ? this.colLeftWidth : this.colMidWidth,
      },
      event,
      (k, value) => {
        if (k === 'col-left') this.colLeftWidth = value;
        if (k === 'col-right') this.colMidWidth = value;
      },
    );
  }

  runAnalysisAndScroll(): void {
    if (this.runBlockedReason) {
      this.runAnalysis();
      return;
    }
    this.runAnalysis();
    this.scrollToWorkspace();
  }

  private scrollToWorkspace(): void {
    window.setTimeout(() => {
      this.workspaceEl?.nativeElement?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }, 80);
  }

  override ngOnDestroy(): void {
    document.body.classList.remove('panel-resizing');
    super.ngOnDestroy();
  }
}

import { Component, ElementRef, OnDestroy, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { InProgressNavButtonComponent } from '../../components/in-progress-nav-button/in-progress-nav-button.component';
import { DualVerifyResultCardComponent } from '../../components/dual-verify-result-card/dual-verify-result-card.component';
import { AnalyseBase } from '../shared/analyse-base';
import { startPanelResize, type PanelResizeKind } from '../shared/panel-resize';

@Component({
  selector: 'app-analyse-v2',
  standalone: true,
  imports: [CommonModule, FormsModule, InProgressNavButtonComponent, DualVerifyResultCardComponent],
  templateUrl: './analyse-v2.component.html',
  styleUrl: './analyse-v2.component.scss',
})
export class AnalyseV2Component extends AnalyseBase implements OnDestroy {
  readonly versionLabel = 'V2 — 3-Column Workspace';
  readonly versionPath = '/analyse-v2';

  @ViewChild('workspaceEl') workspaceEl?: ElementRef<HTMLElement>;

  setupSplitPct = 50;
  colLeftWidth = 300;
  colMidWidth = 300;

  startSetupSplitResize(event: MouseEvent): void {
    const container = (event.target as HTMLElement).closest('.setup-row-inner');
    startPanelResize(
      {
        kind: 'setup-split',
        startX: event.clientX,
        startY: event.clientY,
        startVal: this.setupSplitPct,
        containerWidth: container?.clientWidth ?? 0,
      },
      event,
      (_kind, value) => {
        this.setupSplitPct = value;
      },
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
    const blocked = this.runBlockedReason;
    if (blocked) {
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

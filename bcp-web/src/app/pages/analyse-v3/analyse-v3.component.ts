import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { InProgressNavButtonComponent } from '../../components/in-progress-nav-button/in-progress-nav-button.component';
import { AnalyseBase } from '../shared/analyse-base';
import { startPanelResize, type PanelResizeKind } from '../shared/panel-resize';

type DocViewMode = 'list' | 'table';

@Component({
  selector: 'app-analyse-v3',
  standalone: true,
  imports: [CommonModule, FormsModule, InProgressNavButtonComponent],
  templateUrl: './analyse-v3.component.html',
  styleUrl: './analyse-v3.component.scss',
})
export class AnalyseV3Component extends AnalyseBase implements OnDestroy {
  readonly versionLabel = 'V3 — Command Bar';
  readonly versionPath = '/analyse-v3';

  docViewMode: DocViewMode = 'list';
  docsPanelHeight = 180;
  setupSplitPct = 50;
  colLeftWidth = 280;
  colMidWidth = 280;

  setDocViewMode(mode: DocViewMode): void {
    this.docViewMode = mode;
  }

  startDocsHeightResize(event: MouseEvent): void {
    startPanelResize(
      {
        kind: 'docs-height',
        startX: event.clientX,
        startY: event.clientY,
        startVal: this.docsPanelHeight,
      },
      event,
      (_kind, value) => {
        this.docsPanelHeight = value;
      },
    );
  }

  startSetupSplitResize(event: MouseEvent): void {
    const container = (event.target as HTMLElement).closest('.v3-docs-inner');
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

  override ngOnDestroy(): void {
    document.body.classList.remove('panel-resizing');
    super.ngOnDestroy();
  }
}

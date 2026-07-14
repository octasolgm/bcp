export type PanelResizeKind =
  | 'setup-split'
  | 'docs-height'
  | 'col-left'
  | 'col-right'
  | 'result-split';

export interface PanelResizeStart {
  kind: PanelResizeKind;
  startX: number;
  startY: number;
  startVal: number;
  containerWidth?: number;
}

export interface PanelResizeLimits {
  min: number;
  max: number;
}

const DEFAULT_LIMITS: Record<PanelResizeKind, PanelResizeLimits> = {
  'setup-split': { min: 22, max: 78 },
  'docs-height': { min: 120, max: 420 },
  'col-left': { min: 200, max: 560 },
  'col-right': { min: 200, max: 560 },
  'result-split': { min: 96, max: 420 },
};

/** Pointer-driven panel resize with body cursor lock and clamped values. */
export function startPanelResize(
  start: PanelResizeStart,
  event: MouseEvent,
  apply: (kind: PanelResizeKind, value: number) => void,
  limits?: Partial<Record<PanelResizeKind, PanelResizeLimits>>,
): void {
  event.preventDefault();

  const body = document.body;
  const isRow = start.kind === 'docs-height' || start.kind === 'result-split';
  body.classList.add('panel-resizing');
  body.style.userSelect = 'none';
  body.style.cursor = isRow ? 'row-resize' : 'col-resize';

  const lim = { ...DEFAULT_LIMITS, ...limits };

  const onMove = (e: MouseEvent) => {
    let next = start.startVal;

    if (start.kind === 'docs-height' || start.kind === 'result-split') {
      next = start.startVal + (e.clientY - start.startY);
    } else if (start.kind === 'setup-split' && start.containerWidth) {
      const deltaPct = ((e.clientX - start.startX) / start.containerWidth) * 100;
      next = start.startVal + deltaPct;
    } else {
      next = start.startVal + (e.clientX - start.startX);
    }

    const bounds = lim[start.kind];
    next = Math.min(bounds.max, Math.max(bounds.min, next));
    apply(start.kind, next);
  };

  const onUp = () => {
    window.removeEventListener('mousemove', onMove);
    window.removeEventListener('mouseup', onUp);
    body.classList.remove('panel-resizing');
    body.style.userSelect = '';
    body.style.cursor = '';
  };

  window.addEventListener('mousemove', onMove);
  window.addEventListener('mouseup', onUp);
}

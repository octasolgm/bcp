import { Injectable, signal } from '@angular/core';

export type WorkspaceId = 'snb-uae-difc' | 'snb-uae-adgm' | 'snb-ksa';

export type Workspace = {
  id: WorkspaceId;
  label: string;
  subtitle: string;
};

const STORAGE_KEY = 'reguliq-workspace';

const WORKSPACES: Workspace[] = [
  { id: 'snb-uae-difc', label: 'SNB UAE / DIFC', subtitle: 'Dubai International Financial Centre' },
  { id: 'snb-uae-adgm', label: 'SNB UAE / ADGM', subtitle: 'Abu Dhabi Global Market' },
  { id: 'snb-ksa', label: 'SNB KSA', subtitle: 'Saudi Arabia head office' },
];

@Injectable({ providedIn: 'root' })
export class WorkspaceService {
  readonly workspaces = WORKSPACES;
  private readonly selectedId = signal<WorkspaceId>(this.readStored());

  readonly current = signal(this.find(this.selectedId()));

  setWorkspace(id: WorkspaceId): void {
    this.selectedId.set(id);
    localStorage.setItem(STORAGE_KEY, id);
    this.current.set(this.find(id));
  }

  private find(id: WorkspaceId): Workspace {
    return WORKSPACES.find((w) => w.id === id) ?? WORKSPACES[0];
  }

  private readStored(): WorkspaceId {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored === 'snb-uae-difc' || stored === 'snb-uae-adgm' || stored === 'snb-ksa') return stored;
    return 'snb-uae-difc';
  }
}

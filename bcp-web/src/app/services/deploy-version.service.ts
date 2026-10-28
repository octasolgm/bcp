import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';

export type DeployVersionInfo = {
  label: string;
  api?: string;
  web?: string;
  commit?: string;
  branch?: string;
  builtAt?: string;
  notes?: string;
  persistence?: string;
  bootstrap?: string;
};

@Injectable({ providedIn: 'root' })
export class DeployVersionService {
  private readonly http = inject(HttpClient);

  web: DeployVersionInfo | null = null;
  api: DeployVersionInfo | null = null;
  loadError = '';

  private loadPromise: Promise<void> | null = null;

  async ensureLoaded(): Promise<void> {
    if (this.web && this.api) return;
    if (this.loadPromise) return this.loadPromise;
    this.loadPromise = this.load();
    try {
      await this.loadPromise;
    } finally {
      this.loadPromise = null;
    }
  }

  private async load(): Promise<void> {
    this.loadError = '';
    const [webRes, apiRes] = await Promise.allSettled([
      firstValueFrom(this.http.get<DeployVersionInfo>('/assets/deploy-version.json', { params: { t: Date.now() } })),
      firstValueFrom(
        this.http.get<DeployVersionInfo>(
          `${(environment.ndApiUrl || environment.apiUrl).replace(/\/+$/, '')}/health/version`,
        ),
      ),
    ]);

    if (webRes.status === 'fulfilled') {
      this.web = webRes.value;
    }
    if (apiRes.status === 'fulfilled') {
      this.api = apiRes.value;
    }
    if (webRes.status === 'rejected' && apiRes.status === 'rejected') {
      this.loadError = 'Could not load deploy version info';
    }
  }

  webLabel(): string {
    return this.web?.web ?? this.web?.label ?? 'unknown';
  }

  apiLabel(): string {
    return this.api?.api ?? this.api?.label ?? 'unknown';
  }

  versionsMatch(): boolean {
    if (!this.web || !this.api) return true;
    const w = (this.web.web ?? this.web.label ?? '').trim();
    const a = (this.api.api ?? this.api.label ?? '').trim();
    if (!w || !a || w === 'unknown' || a === 'unknown' || w === 'dev' || a === 'dev') return true;
    return w === a;
  }

  formatBuiltAt(iso?: string | null): string {
    if (!iso?.trim()) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
  }
}

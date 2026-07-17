import { Injectable } from '@angular/core';
import { applyRuntimeConfig, type RuntimeAppConfig } from '../../environments/runtime-config';

@Injectable({ providedIn: 'root' })
export class AppConfigService {
  async load(): Promise<void> {
    try {
      const res = await fetch('/assets/app-config.json', { cache: 'no-store' });
      if (!res.ok) return;
      const cfg = (await res.json()) as RuntimeAppConfig;
      applyRuntimeConfig(cfg);
    } catch {
      // Build-time environment.ts / environment.production.ts values remain in use.
    }
  }
}

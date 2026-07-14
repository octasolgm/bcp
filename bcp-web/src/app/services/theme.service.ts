import { Injectable, signal, computed, effect } from '@angular/core';

export type ThemeMode = 'dark' | 'light' | 'system';
export type ResolvedTheme = 'dark' | 'light';

const STORAGE_KEY = 'reguliq-theme';
const THEME_COLORS: Record<ResolvedTheme, string> = {
  dark: '#0b121e',
  light: '#f1f5f9',
};

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly mode = signal<ThemeMode>(this.readStored());
  private readonly media = window.matchMedia('(prefers-color-scheme: dark)');

  readonly resolvedTheme = computed<ResolvedTheme>(() => {
    const m = this.mode();
    if (m === 'system') {
      return this.media.matches ? 'dark' : 'light';
    }
    return m;
  });

  readonly currentMode = this.mode.asReadonly();

  constructor() {
    this.applyTheme(this.resolvedTheme());

    effect(() => {
      this.applyTheme(this.resolvedTheme());
    });

    this.media.addEventListener('change', () => {
      if (this.mode() === 'system') {
        this.applyTheme(this.resolvedTheme());
      }
    });
  }

  setMode(mode: ThemeMode): void {
    this.mode.set(mode);
    localStorage.setItem(STORAGE_KEY, mode);
    this.applyTheme(this.resolvedTheme());
  }

  private applyTheme(theme: ResolvedTheme): void {
    document.documentElement.setAttribute('data-theme', theme);
    document.documentElement.style.colorScheme = theme;

    const meta = document.querySelector('meta[name="theme-color"]');
    meta?.setAttribute('content', THEME_COLORS[theme]);
  }

  private readStored(): ThemeMode {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored === 'dark' || stored === 'light' || stored === 'system') return stored;
    return 'dark';
  }
}

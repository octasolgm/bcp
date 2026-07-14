import { TestBed } from '@angular/core/testing';
import { ThemeService } from './theme.service';

describe('ThemeService', () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    document.documentElement.style.colorScheme = '';
    TestBed.configureTestingModule({});
  });

  afterEach(() => {
    localStorage.clear();
    TestBed.resetTestingModule();
  });

  function injectFresh(): ThemeService {
    return TestBed.inject(ThemeService);
  }

  it('defaults to dark theme', () => {
    const service = injectFresh();
    expect(service.currentMode()).toBe('dark');
    expect(service.resolvedTheme()).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('applies light theme and persists preference', () => {
    const service = injectFresh();
    service.setMode('light');

    expect(service.currentMode()).toBe('light');
    expect(service.resolvedTheme()).toBe('light');
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    expect(document.documentElement.style.colorScheme).toBe('light');
    expect(localStorage.getItem('reguliq-theme')).toBe('light');
  });

  it('restores stored theme on init', () => {
    localStorage.setItem('reguliq-theme', 'light');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({});
    const restored = TestBed.inject(ThemeService);

    expect(restored.currentMode()).toBe('light');
    expect(restored.resolvedTheme()).toBe('light');
  });

  it('updates meta theme-color when theme changes', () => {
    let meta = document.querySelector('meta[name="theme-color"]');
    if (!meta) {
      meta = document.createElement('meta');
      meta.setAttribute('name', 'theme-color');
      document.head.appendChild(meta);
    }

    const service = injectFresh();
    service.setMode('light');
    expect(meta.getAttribute('content')).toBe('#f1f5f9');

    service.setMode('dark');
    expect(meta.getAttribute('content')).toBe('#0b121e');
  });

  it('resolves system mode from prefers-color-scheme', () => {
    spyOn(window, 'matchMedia').and.returnValue({
      matches: false,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    } as unknown as MediaQueryList);

    localStorage.setItem('reguliq-theme', 'system');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({});
    const systemService = TestBed.inject(ThemeService);

    expect(systemService.currentMode()).toBe('system');
    expect(systemService.resolvedTheme()).toBe('light');
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
  });
});

import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { ShellComponent } from './shell.component';
import { ThemeService } from '../services/theme.service';

describe('ShellComponent theme integration', () => {
  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [ShellComponent],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
  });

  afterEach(() => {
    localStorage.clear();
    TestBed.resetTestingModule();
  });

  it('opens settings and switches to light theme', () => {
    const fixture = TestBed.createComponent(ShellComponent);
    fixture.detectChanges();

    const settingsBtn = fixture.nativeElement.querySelector('button[aria-label="Settings"]');
    settingsBtn.click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.settings-panel')).toBeTruthy();

    const lightBtn = Array.from<HTMLButtonElement>(
      fixture.nativeElement.querySelectorAll('.theme-option'),
    ).find((btn) => btn.textContent?.includes('Light'));
    lightBtn?.click();
    fixture.detectChanges();

    const theme = TestBed.inject(ThemeService);
    expect(theme.currentMode()).toBe('light');
    expect(theme.resolvedTheme()).toBe('light');
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
  });
});

import { Component, Input } from '@angular/core';
import { BRAND } from '../../config/brand';

export type BrandLogoVariant = 'header' | 'primary' | 'mark';

@Component({
  selector: 'app-brand-logo',
  standalone: true,
  template: `
    @if (variant === 'mark') {
      <img
        class="brand-logo brand-logo-mark-only"
        [src]="BRAND.logoMark"
        alt=""
        decoding="async"
      />
    } @else {
      <span
        class="brand-lockup"
        [class.brand-lockup-header]="variant === 'header'"
        [class.brand-lockup-primary]="variant === 'primary'"
      >
        <img
          class="brand-logo brand-logo-icon"
          [src]="BRAND.logo"
          alt=""
          decoding="async"
        />
        <span class="brand-name">{{ BRAND.name }}</span>
      </span>
    }
  `,
  styles: [
    `
      :host {
        display: inline-flex;
        align-items: center;
        max-width: 100%;
      }

      .brand-lockup {
        display: inline-flex;
        align-items: center;
        gap: clamp(0.5rem, 1.2vw, 0.75rem);
        min-width: 0;
        max-width: 100%;
      }

      .brand-logo {
        display: block;
        flex-shrink: 0;
        object-fit: contain;
      }

      /* Standalone icon */
      .brand-logo-mark-only {
        width: clamp(2.5rem, 6vw, 3.25rem);
        height: clamp(2.5rem, 6vw, 3.25rem);
      }

      /* Header: icon scales with viewport, stays readable on mobile */
      .brand-lockup-header .brand-logo-icon {
        width: clamp(2.75rem, 5.5vw, 3.5rem);
        height: clamp(2.75rem, 5.5vw, 3.5rem);
      }

      .brand-lockup-header .brand-name {
        font-size: clamp(1.0625rem, 2.2vw, 1.25rem);
      }

      /* Login / hero */
      .brand-lockup-primary .brand-logo-icon {
        width: clamp(3.25rem, 8vw, 4.5rem);
        height: clamp(3.25rem, 8vw, 4.5rem);
      }

      .brand-lockup-primary .brand-name {
        font-size: clamp(1.375rem, 3.5vw, 1.75rem);
      }

      .brand-name {
        font-weight: 700;
        letter-spacing: -0.02em;
        line-height: 1.1;
        color: var(--text-primary, #0f172a);
        white-space: nowrap;
      }

      @media (max-width: 480px) {
        .brand-lockup-header .brand-name {
          font-size: 1rem;
        }
      }
    `,
  ],
})
export class BrandLogoComponent {
  readonly BRAND = BRAND;

  @Input() variant: BrandLogoVariant = 'header';
}

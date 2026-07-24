import { Component, Input } from '@angular/core';
import { BRAND } from '../../config/brand';

export type BrandLogoVariant = 'header' | 'primary' | 'mark';

@Component({
  selector: 'app-brand-logo',
  standalone: true,
  template: `
    <img
      class="brand-logo"
      [class.brand-logo-header]="variant === 'header'"
      [class.brand-logo-primary]="variant === 'primary'"
      [class.brand-logo-mark]="variant === 'mark'"
      [src]="src"
      [alt]="BRAND.name"
      decoding="async"
    />
  `,
  styles: [
    `
      :host {
        display: inline-flex;
        align-items: center;
        max-width: 100%;
      }

      .brand-logo {
        display: block;
        max-width: 100%;
        height: auto;
        object-fit: contain;
      }

      .brand-logo-header {
        height: 28px;
        width: auto;
        max-width: min(220px, 42vw);
      }

      .brand-logo-primary {
        height: 40px;
        width: auto;
        max-width: min(320px, 88vw);
      }

      .brand-logo-mark {
        height: 32px;
        width: 32px;
      }
    `,
  ],
})
export class BrandLogoComponent {
  readonly BRAND = BRAND;

  @Input() variant: BrandLogoVariant = 'header';

  get src(): string {
    switch (this.variant) {
      case 'primary':
        return BRAND.logoPrimary;
      case 'mark':
        return BRAND.logoMark;
      default:
        return BRAND.logoHeader;
    }
  }
}

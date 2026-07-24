import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import type { DualVerifyLlmProviderOption, DualVerifyLlmSettings } from '../../../../lib/nd/types';

@Component({
  selector: 'app-nd-admin-settings',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-admin-settings.component.html',
  styleUrls: ['./nd-admin-settings.component.scss', '../nd-shared.scss'],
})
export class NdAdminSettingsComponent implements OnInit {
  private readonly api = inject(NdApiService);
  readonly auth = inject(NdAuthService);

  loading = true;
  saving = false;
  error = '';
  message = '';
  settings: DualVerifyLlmSettings | null = null;
  selectedProvider = 'google';
  selectedModel = '';

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    await this.load();
  }

  get isSuperAdmin(): boolean {
    return this.auth.getRole() === 'super_admin';
  }

  get providerOptions(): DualVerifyLlmProviderOption[] {
    return this.settings?.providers ?? [];
  }

  get selectedProviderMeta(): DualVerifyLlmProviderOption | undefined {
    return this.providerOptions.find((p) => p.id === this.selectedProvider);
  }

  get modelOptions(): string[] {
    return this.selectedProviderMeta?.models ?? [];
  }

  get selectedProviderConfigured(): boolean {
    return this.selectedProviderMeta?.apiKeyConfigured ?? false;
  }

  onProviderChange(): void {
    const meta = this.selectedProviderMeta;
    if (!meta) return;
    if (!meta.models.includes(this.selectedModel)) {
      this.selectedModel = meta.defaultModel;
    }
  }

  async load(): Promise<void> {
    this.loading = true;
    this.error = '';
    const res = await this.api.getDualVerifyLlmSettings();
    this.loading = false;
    if (!res.success || !res.data) {
      this.error = res.message ?? 'Failed to load settings';
      return;
    }
    this.settings = res.data;
    this.selectedProvider = res.data.provider;
    this.selectedModel = res.data.model;
  }

  async save(): Promise<void> {
    if (!this.selectedProvider || !this.selectedModel) return;
    this.saving = true;
    this.error = '';
    this.message = '';
    const res = await this.api.updateDualVerifyLlmSettings({
      provider: this.selectedProvider,
      model: this.selectedModel,
    });
    this.saving = false;
    if (!res.success || !res.data) {
      this.error = this.friendlyError(res.message ?? 'Failed to save settings');
      return;
    }
    this.settings = res.data;
    this.selectedProvider = res.data.provider;
    this.selectedModel = res.data.model;
    this.message = 'Your choice was saved. New analyses will use this model for Pass 2.';
  }

  private friendlyError(raw: string): string {
    if (raw.includes('DbUpdateException') || raw.includes('PostgresException') || raw.includes('42804')) {
      return 'Could not save settings. Please try again or contact your administrator.';
    }
    if (raw.length > 280) {
      return 'Could not save settings. Please try again.';
    }
    return raw;
  }
}

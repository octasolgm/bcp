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

  regulSettings: DualVerifyLlmSettings | null = null;
  regulSelectedProvider = 'google';
  regulSelectedModel = '';
  regulSaving = false;
  regulError = '';
  regulMessage = '';

  finalizeEmbedSettings: DualVerifyLlmSettings | null = null;
  finalizeEmbedProvider = 'google';
  finalizeEmbedModel = '';
  finalizeEmbedSaving = false;
  finalizeEmbedError = '';
  finalizeEmbedMessage = '';

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

  get regulProviderOptions(): DualVerifyLlmProviderOption[] {
    return this.regulSettings?.providers ?? [];
  }

  get regulSelectedProviderMeta(): DualVerifyLlmProviderOption | undefined {
    return this.regulProviderOptions.find((p) => p.id === this.regulSelectedProvider);
  }

  get regulModelOptions(): string[] {
    return this.regulSelectedProviderMeta?.models ?? [];
  }

  get regulSelectedProviderConfigured(): boolean {
    return this.regulSelectedProviderMeta?.apiKeyConfigured ?? false;
  }

  get finalizeEmbedProviderOptions(): DualVerifyLlmProviderOption[] {
    return this.finalizeEmbedSettings?.providers ?? [];
  }

  get finalizeEmbedProviderMeta(): DualVerifyLlmProviderOption | undefined {
    return this.finalizeEmbedProviderOptions.find((p) => p.id === this.finalizeEmbedProvider);
  }

  get finalizeEmbedModelOptions(): string[] {
    return this.finalizeEmbedProviderMeta?.models ?? [];
  }

  get finalizeEmbedProviderConfigured(): boolean {
    return this.finalizeEmbedProviderMeta?.apiKeyConfigured ?? false;
  }

  onProviderChange(): void {
    const meta = this.selectedProviderMeta;
    if (!meta) return;
    if (!meta.models.includes(this.selectedModel)) {
      this.selectedModel = meta.defaultModel;
    }
  }

  onRegulProviderChange(): void {
    const meta = this.regulSelectedProviderMeta;
    if (!meta) return;
    if (!meta.models.includes(this.regulSelectedModel)) {
      this.regulSelectedModel = meta.defaultModel;
    }
  }

  onFinalizeEmbedProviderChange(): void {
    const meta = this.finalizeEmbedProviderMeta;
    if (!meta) return;
    if (!meta.models.includes(this.finalizeEmbedModel)) {
      this.finalizeEmbedModel = meta.defaultModel;
    }
  }

  async load(): Promise<void> {
    this.loading = true;
    this.error = '';
    const [res, regulRes, finalizeEmbedRes] = await Promise.all([
      this.api.getDualVerifyLlmSettings(),
      this.api.getRegulWorkflowLlmSettings(),
      this.api.getFinalizeEmbedLlmSettings(),
    ]);
    this.loading = false;
    if (!res.success || !res.data) {
      this.error = res.message ?? 'Failed to load settings';
      return;
    }
    this.settings = res.data;
    this.selectedProvider = res.data.provider;
    this.selectedModel = res.data.model;

    if (regulRes.success && regulRes.data) {
      this.regulSettings = regulRes.data;
      this.regulSelectedProvider = regulRes.data.provider;
      this.regulSelectedModel = regulRes.data.model;
    }

    if (finalizeEmbedRes.success && finalizeEmbedRes.data) {
      this.finalizeEmbedSettings = finalizeEmbedRes.data;
      this.finalizeEmbedProvider = finalizeEmbedRes.data.provider;
      this.finalizeEmbedModel = finalizeEmbedRes.data.model;
    }
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

  async saveRegul(): Promise<void> {
    if (!this.regulSelectedProvider || !this.regulSelectedModel) return;
    this.regulSaving = true;
    this.regulError = '';
    this.regulMessage = '';
    const res = await this.api.updateRegulWorkflowLlmSettings({
      provider: this.regulSelectedProvider,
      model: this.regulSelectedModel,
    });
    this.regulSaving = false;
    if (!res.success || !res.data) {
      this.regulError = this.friendlyError(res.message ?? 'Failed to save settings');
      return;
    }
    this.regulSettings = res.data;
    this.regulSelectedProvider = res.data.provider;
    this.regulSelectedModel = res.data.model;
    this.regulMessage = 'Saved. New Regul workflow analyses will use this model.';
  }

  async saveFinalizeEmbed(): Promise<void> {
    if (!this.finalizeEmbedProvider || !this.finalizeEmbedModel) return;
    this.finalizeEmbedSaving = true;
    this.finalizeEmbedError = '';
    this.finalizeEmbedMessage = '';
    const res = await this.api.updateFinalizeEmbedLlmSettings({
      provider: this.finalizeEmbedProvider,
      model: this.finalizeEmbedModel,
    });
    this.finalizeEmbedSaving = false;
    if (!res.success || !res.data) {
      this.finalizeEmbedError = this.friendlyError(res.message ?? 'Failed to save settings');
      return;
    }
    this.finalizeEmbedSettings = res.data;
    this.finalizeEmbedProvider = res.data.provider;
    this.finalizeEmbedModel = res.data.model;
    this.finalizeEmbedMessage =
      'Saved. When a reviewer finalizes a run, this model will draft policy text for corrected documents.';
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

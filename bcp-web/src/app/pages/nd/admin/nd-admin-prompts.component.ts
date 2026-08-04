import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { startPanelResize } from '../../shared/panel-resize';
import type {
  AnalysisPromptCoverage,
  AnalysisPromptDefinition,
  AnalysisPromptSuggestion,
  AnalysisPromptVersion,
  DualVerifyLlmProviderOption,
} from '../../../../lib/nd/types';

@Component({
  selector: 'app-nd-admin-prompts',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-admin-prompts.component.html',
  styleUrls: ['./nd-admin-prompts.component.scss', '../nd-shared.scss'],
})
export class NdAdminPromptsComponent implements OnInit {
  private static readonly VERSIONS_PANEL_KEY = 'nd-admin-prompts-versions-panel';

  private readonly api = inject(NdApiService);
  readonly auth = inject(NdAuthService);

  loading = true;
  error = '';
  message = '';
  workflows: { workflow: string; prompts: AnalysisPromptDefinition[] }[] = [];
  allPrompts: AnalysisPromptDefinition[] = [];
  selectedWorkflow = '';
  selectedPromptKey: string | null = null;
  selectedVersionId: string | null = null;
  draftText = '';
  expandedPromptKeys = new Set<string>();
  newCommentByKey: Record<string, string> = {};
  editingId: string | null = null;
  editingText = '';
  savingKey: string | null = null;
  deletingId: string | null = null;
  savingVersion = false;
  settingCurrentId: string | null = null;
  versionsPanelPct = 28;

  llmProviders: DualVerifyLlmProviderOption[] = [];
  generateProvider = '';
  generateModel = '';
  generateInstruction = '';
  generating = false;
  selectedSuggestionIds = new Set<string>();
  lastCoverage: AnalysisPromptCoverage[] = [];
  lastAppliedSuggestionIds = new Set<string>();

  async ngOnInit(): Promise<void> {
    const saved = localStorage.getItem(NdAdminPromptsComponent.VERSIONS_PANEL_KEY);
    if (saved) {
      const n = Number(saved);
      if (!Number.isNaN(n)) this.versionsPanelPct = Math.min(45, Math.max(18, n));
    }
    await this.auth.refreshProfile();
    await Promise.all([this.load(), this.loadLlmProviders()]);
  }

  async loadLlmProviders(): Promise<void> {
    const res = await this.api.getAnalysisPromptLlmProviders();
    if (!res.success || !res.data) return;
    this.llmProviders = res.data;
    const configured = this.llmProviders.find((p) => p.apiKeyConfigured);
    const first = configured ?? this.llmProviders[0];
    if (first) {
      this.generateProvider = first.id;
      this.generateModel = first.defaultModel;
    }
  }

  get isSuperAdmin(): boolean {
    return this.auth.getRole() === 'super_admin';
  }

  get activeWorkflows(): { workflow: string; prompts: AnalysisPromptDefinition[] }[] {
    if (!this.selectedWorkflow) return this.workflows;
    return this.workflows.filter((w) => w.workflow === this.selectedWorkflow);
  }

  get activePrompts(): AnalysisPromptDefinition[] {
    return this.activeWorkflows.flatMap((w) => w.prompts);
  }

  get selectedPrompt(): AnalysisPromptDefinition | null {
    if (!this.selectedPromptKey) return null;
    return this.allPrompts.find((p) => p.key === this.selectedPromptKey) ?? null;
  }

  get selectedVersion(): AnalysisPromptVersion | null {
    const prompt = this.selectedPrompt;
    if (!prompt || !this.selectedVersionId) return null;
    return prompt.versions.find((v) => v.id === this.selectedVersionId) ?? null;
  }

  get layoutColumns(): string {
    if (!this.selectedPrompt) return '1fr';
    const right = this.versionsPanelPct;
    const left = 100 - right;
    return `minmax(0, ${left}%) 10px minmax(14rem, ${right}%)`;
  }

  get draftDirty(): boolean {
    const version = this.selectedVersion;
    if (!version) return false;
    return this.draftText.trim() !== version.promptText.trim();
  }

  get isRegulWorkflow(): boolean {
    return this.selectedWorkflow.includes('Regul workflow');
  }

  get judgmentPromptParts(): AnalysisPromptDefinition[] {
    return this.activePrompts.filter((p) => p.key.startsWith('regul_judgment'));
  }

  partShortLabel(prompt: AnalysisPromptDefinition): string {
    if (prompt.key === 'regul_judgment_system') return 'System';
    if (prompt.key === 'regul_judgment_user_context') return 'User block 1';
    if (prompt.key === 'regul_judgment_user_query') return 'User block 2';
    return prompt.label;
  }

  currentVersion(prompt: AnalysisPromptDefinition): AnalysisPromptVersion | null {
    return prompt.versions.find((v) => v.isCurrent) ?? prompt.versions[0] ?? null;
  }

  async load(): Promise<void> {
    this.loading = true;
    this.error = '';
    const res = await this.api.getAnalysisPrompts();
    this.loading = false;
    if (!res.success || !res.data) {
      this.error = res.message ?? 'Failed to load prompts';
      return;
    }
    this.workflows = res.data.workflows ?? [];
    this.allPrompts = res.data.prompts ?? [];
    if (!this.selectedWorkflow && this.workflows.length > 0) {
      this.selectedWorkflow = this.workflows[0].workflow;
    }
    if (this.expandedPromptKeys.size === 0 && this.activePrompts.length > 0) {
      this.selectPrompt(this.activePrompts[0]);
    } else if (this.selectedPromptKey) {
      const prompt = this.allPrompts.find((p) => p.key === this.selectedPromptKey);
      if (prompt) this.syncDraftFromSelection(prompt);
    }
  }

  selectWorkflow(workflow: string): void {
    this.selectedWorkflow = workflow;
    this.expandedPromptKeys.clear();
    const first = this.activePrompts[0];
    if (first) this.selectPrompt(first);
  }

  selectPrompt(prompt: AnalysisPromptDefinition, collapseOthers = false): void {
    if (collapseOthers) {
      this.expandedPromptKeys.clear();
    }
    this.selectedPromptKey = prompt.key;
    this.expandedPromptKeys.add(prompt.key);
    const current =
      prompt.versions.find((v) => v.id === prompt.currentVersionId)
      ?? prompt.versions.find((v) => v.isCurrent)
      ?? prompt.versions[0];
    this.selectedVersionId = current?.id ?? null;
    this.draftText = current?.promptText ?? prompt.text;
    this.message = '';
    this.resetGenerateState();
  }

  private resetGenerateState(): void {
    this.selectedSuggestionIds = new Set<string>();
    this.lastCoverage = [];
    this.lastAppliedSuggestionIds = new Set<string>();
    this.generateInstruction = '';
  }

  selectVersion(version: AnalysisPromptVersion): void {
    this.selectedVersionId = version.id;
    this.draftText = version.promptText;
  }

  togglePrompt(prompt: AnalysisPromptDefinition): void {
    if (this.expandedPromptKeys.has(prompt.key) && this.selectedPromptKey === prompt.key) {
      this.expandedPromptKeys.delete(prompt.key);
    } else {
      this.selectPrompt(prompt);
    }
  }

  isExpanded(key: string): boolean {
    return this.expandedPromptKeys.has(key);
  }

  suggestionCount(prompt: AnalysisPromptDefinition): number {
    return prompt.suggestions?.length ?? 0;
  }

  startVersionsResize(event: MouseEvent): void {
    const layout = (event.target as HTMLElement).closest('.prompts-layout');
    const containerWidth = layout?.clientWidth ?? 1200;
    const rightPct = this.versionsPanelPct;
    startPanelResize(
      {
        kind: 'setup-split',
        startX: event.clientX,
        startY: event.clientY,
        startVal: 100 - rightPct,
        containerWidth,
      },
      event,
      (_kind, leftPct) => {
        this.versionsPanelPct = 100 - leftPct;
      },
      { 'setup-split': { min: 55, max: 82 } },
    );
    const onUp = () => {
      window.removeEventListener('mouseup', onUp);
      localStorage.setItem(
        NdAdminPromptsComponent.VERSIONS_PANEL_KEY,
        String(this.versionsPanelPct),
      );
    };
    window.addEventListener('mouseup', onUp);
  }

  onDraftChange(prompt: AnalysisPromptDefinition, value: string): void {
    if (this.selectedPromptKey !== prompt.key) return;
    this.draftText = value;
    this.error = '';
  }

  requiredTags(prompt: AnalysisPromptDefinition): string[] {
    if (prompt.key === 'regul_judgment_user_context') return ['{policy_context}'];
    if (prompt.key === 'regul_judgment_user_query') return ['{clause_no}', '{clause_text}'];
    return [];
  }

  missingTags(prompt: AnalysisPromptDefinition, text: string): string[] {
    return this.requiredTags(prompt).filter((tag) => !text.includes(tag));
  }

  hasTagValidationErrors(prompt: AnalysisPromptDefinition, text: string): boolean {
    return this.missingTags(prompt, text).length > 0;
  }

  validatePromptText(prompt: AnalysisPromptDefinition, text: string): string | null {
    const missing = this.missingTags(prompt, text);
    if (missing.length === 0) return null;
    if (prompt.key === 'regul_judgment_user_context') {
      return 'User block 1 must include {policy_context} where policy excerpts are inserted at runtime.';
    }
    if (prompt.key === 'regul_judgment_user_query') {
      return `User block 2 must include mandatory tags: ${missing.join(', ')}.`;
    }
    return `Missing mandatory tags: ${missing.join(', ')}.`;
  }

  placeholderHint(prompt: AnalysisPromptDefinition): string | null {
    if (prompt.key === 'regul_judgment_user_context') {
      return 'At runtime, {policy_context} is replaced with retrieved internal policy excerpts for each clause.';
    }
    if (prompt.key === 'regul_judgment_user_query') {
      return 'At runtime, {clause_no} and {clause_text} are replaced with the regulatory clause being judged.';
    }
    return null;
  }

  get selectedProviderOption(): DualVerifyLlmProviderOption | null {
    return this.llmProviders.find((p) => p.id === this.generateProvider) ?? null;
  }

  onProviderChange(): void {
    const opt = this.selectedProviderOption;
    this.generateModel = opt?.defaultModel ?? '';
  }

  isSuggestionSelected(suggestion: AnalysisPromptSuggestion): boolean {
    return this.selectedSuggestionIds.has(suggestion.id);
  }

  toggleSuggestionSelected(suggestion: AnalysisPromptSuggestion): void {
    if (this.selectedSuggestionIds.has(suggestion.id)) {
      this.selectedSuggestionIds.delete(suggestion.id);
    } else {
      this.selectedSuggestionIds.add(suggestion.id);
    }
    // Editing selection invalidates any previously generated coverage preview.
    this.lastCoverage = [];
  }

  coverageFor(suggestion: AnalysisPromptSuggestion): AnalysisPromptCoverage | undefined {
    return this.lastCoverage.find((c) => c.suggestionId === suggestion.id);
  }

  get canGenerate(): boolean {
    return (
      !this.generating &&
      !!this.generateProvider &&
      !!this.selectedProviderOption?.apiKeyConfigured &&
      this.selectedSuggestionIds.size > 0
    );
  }

  async generateWithAi(prompt: AnalysisPromptDefinition): Promise<void> {
    if (!this.canGenerate) return;

    this.generating = true;
    this.error = '';
    this.message = '';
    const res = await this.api.generateAnalysisPromptVersion({
      promptKey: prompt.key,
      suggestionIds: Array.from(this.selectedSuggestionIds),
      provider: this.generateProvider,
      model: this.generateModel || undefined,
      instruction: this.generateInstruction.trim() || undefined,
    });
    this.generating = false;
    if (!res.success || !res.data) {
      this.error = res.message ?? 'Failed to generate a new prompt with AI';
      return;
    }

    this.draftText = res.data.promptText;
    this.lastCoverage = res.data.coverage;
    this.lastAppliedSuggestionIds = new Set(this.selectedSuggestionIds);
    const providerLabel = this.selectedProviderOption?.label ?? this.generateProvider;
    this.message = `${providerLabel} drafted a new prompt from ${this.selectedSuggestionIds.size} suggestion(s). Review the checklist below, optimize the text if needed, then save as a new version.`;
  }

  async saveNewVersion(prompt: AnalysisPromptDefinition): Promise<void> {
    const text = this.draftText.trim();
    if (!text) return;

    const validationError = this.validatePromptText(prompt, text);
    if (validationError) {
      this.error = validationError;
      return;
    }

    this.savingVersion = true;
    this.error = '';
    this.message = '';
    const appliedSuggestionIds = Array.from(this.lastAppliedSuggestionIds);
    const res = await this.api.createAnalysisPromptVersion({
      promptKey: prompt.key,
      promptText: text,
      appliedSuggestionIds: appliedSuggestionIds.length > 0 ? appliedSuggestionIds : undefined,
    });
    this.savingVersion = false;
    if (!res.success || !res.data) {
      this.error = res.message ?? 'Failed to save version';
      return;
    }
    prompt.versions = [res.data, ...prompt.versions];
    this.selectedVersionId = res.data.id;
    this.draftText = res.data.promptText;
    if (appliedSuggestionIds.length > 0) {
      prompt.suggestions = prompt.suggestions.map((s) =>
        appliedSuggestionIds.includes(s.id) ? { ...s, appliedInVersionId: res.data!.id } : s,
      );
    }
    this.resetGenerateState();
    this.message = `Saved as version ${res.data.versionNumber}. Set it as current to use it in Analysis V3.`;
  }

  async setCurrentVersion(prompt: AnalysisPromptDefinition, version: AnalysisPromptVersion): Promise<void> {
    if (version.isCurrent) return;

    const validationError = this.validatePromptText(prompt, version.promptText);
    if (validationError) {
      this.error = validationError;
      return;
    }

    this.settingCurrentId = version.id;
    this.error = '';
    this.message = '';
    const res = await this.api.setCurrentAnalysisPromptVersion(version.id);
    this.settingCurrentId = null;
    if (!res.success || !res.data) {
      this.error = res.message ?? 'Failed to set current version';
      return;
    }

    prompt.versions = prompt.versions.map((v) => ({
      ...v,
      isCurrent: v.id === version.id,
    }));
    prompt.currentVersionId = version.id;
    prompt.text = version.promptText;
    this.message =
      prompt.key.startsWith('regul_judgment')
        ? `Version ${version.versionNumber} is now current for Analysis V3.`
        : `Version ${version.versionNumber} is now current.`;
  }

  private syncDraftFromSelection(prompt: AnalysisPromptDefinition): void {
    const version =
      prompt.versions.find((v) => v.id === this.selectedVersionId)
      ?? prompt.versions.find((v) => v.isCurrent)
      ?? prompt.versions[0];
    this.selectedVersionId = version?.id ?? null;
    this.draftText = version?.promptText ?? prompt.text;
  }

  async addSuggestion(prompt: AnalysisPromptDefinition): Promise<void> {
    const comment = (this.newCommentByKey[prompt.key] ?? '').trim();
    if (!comment) return;

    this.savingKey = prompt.key;
    this.error = '';
    const res = await this.api.createAnalysisPromptSuggestion({
      promptKey: prompt.key,
      comment,
    });
    this.savingKey = null;
    if (!res.success || !res.data) {
      this.error = res.message ?? 'Failed to add suggestion';
      return;
    }
    this.newCommentByKey[prompt.key] = '';
    prompt.suggestions = [...(prompt.suggestions ?? []), res.data];
  }

  startEdit(suggestion: AnalysisPromptSuggestion): void {
    this.editingId = suggestion.id;
    this.editingText = suggestion.comment;
  }

  cancelEdit(): void {
    this.editingId = null;
    this.editingText = '';
  }

  async saveEdit(suggestion: AnalysisPromptSuggestion, prompt: AnalysisPromptDefinition): Promise<void> {
    const comment = this.editingText.trim();
    if (!comment) return;

    this.savingKey = prompt.key;
    this.error = '';
    const res = await this.api.updateAnalysisPromptSuggestion(suggestion.id, { comment });
    this.savingKey = null;
    if (!res.success || !res.data) {
      this.error = res.message ?? 'Failed to update suggestion';
      return;
    }
    prompt.suggestions = (prompt.suggestions ?? []).map((s) =>
      s.id === suggestion.id ? res.data! : s,
    );
    this.cancelEdit();
  }

  async deleteSuggestion(suggestion: AnalysisPromptSuggestion, prompt: AnalysisPromptDefinition): Promise<void> {
    if (!confirm('Delete this suggestion?')) return;

    this.deletingId = suggestion.id;
    this.error = '';
    const res = await this.api.deleteAnalysisPromptSuggestion(suggestion.id);
    this.deletingId = null;
    if (!res.success) {
      this.error = res.message ?? 'Failed to delete suggestion';
      return;
    }
    prompt.suggestions = (prompt.suggestions ?? []).filter((s) => s.id !== suggestion.id);
  }
}

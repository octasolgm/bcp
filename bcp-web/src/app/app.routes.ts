import { Routes } from '@angular/router';
import { ShellComponent } from './layout/shell.component';
import { authGuard, guestGuard } from './guards/auth.guard';
import { ndAuthGuard, ndGuestGuard } from './guards/nd-auth.guard';
import { ndRoleGuard } from './guards/nd-role.guard';
import { ndDenyDemoViewerGuard } from './guards/nd-deny-demo-viewer.guard';

const ndAnalyseV8Route = {
  canActivate: [ndRoleGuard, ndDenyDemoViewerGuard],
  data: { ndRoles: ['maker', 'super_admin'] },
  loadComponent: () =>
    import('./pages/analyse-v8/analyse-v8.component').then((m) => m.AnalyseV8Component),
};

const ndAnalyseV9Route = {
  canActivate: [ndRoleGuard, ndDenyDemoViewerGuard],
  data: { ndRoles: ['maker', 'super_admin'] },
  loadComponent: () =>
    import('./pages/analyse-v9/analyse-v9.component').then((m) => m.AnalyseV9Component),
};

const ndAnalyseRegulRoute = {
  canActivate: [ndRoleGuard, ndDenyDemoViewerGuard],
  data: { ndRoles: ['maker', 'super_admin'] },
  title: 'Regul Workflow · Comply Solution',
  loadComponent: () =>
    import('./pages/analyse-regul/analyse-regul.component').then((m) => m.AnalyseRegulComponent),
};

const ndAnalyseRegulFullRoute = {
  canActivate: [ndRoleGuard],
  data: { ndRoles: ['maker', 'super_admin'] },
  title: 'Regul Full Markdown · Comply Solution',
  loadComponent: () =>
    import('./pages/analyse-regul-full/analyse-regul-full.component').then(
      (m) => m.AnalyseRegulFullComponent,
    ),
};

/** Legacy app pages (served under /old/*). */
const legacyAppRoutes: Routes = [
  { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
  {
    path: 'dashboard',
    title: 'Dashboard · Comply Solution',
    loadComponent: () =>
      import('./pages/dashboard/dashboard.component').then((m) => m.DashboardComponent),
  },
  {
    path: 'analyse',
    title: 'New Gap Analysis · Comply Solution',
    loadComponent: () =>
      import('./pages/analyse/analyse.component').then((m) => m.AnalyseComponent),
  },
  {
    path: 'analyse-v2',
    title: 'New Gap Analysis V2 · Comply Solution',
    loadComponent: () =>
      import('./pages/analyse-v2/analyse-v2.component').then((m) => m.AnalyseV2Component),
  },
  {
    path: 'analyse-v3',
    title: 'New Gap Analysis V3 · Comply Solution',
    loadComponent: () =>
      import('./pages/analyse-v3/analyse-v3.component').then((m) => m.AnalyseV3Component),
  },
  {
    path: 'analyse-v4',
    title: 'New Gap Analysis V4 · Comply Solution',
    loadComponent: () =>
      import('./pages/analyse-v4/analyse-v4.component').then((m) => m.AnalyseV4Component),
  },
  {
    path: 'analyse-v5',
    title: 'New Gap Analysis V5 · Comply Solution',
    loadComponent: () =>
      import('./pages/analyse-v5/analyse-v5.component').then((m) => m.AnalyseV5Component),
  },
  {
    path: 'analyse-v6',
    title: 'New Gap Analysis V6 · Comply Solution',
    loadComponent: () =>
      import('./pages/analyse-v6/analyse-v6.component').then((m) => m.AnalyseV6Component),
  },
  {
    path: 'analyse-v7',
    title: 'New Gap Analysis V7 · Comply Solution',
    loadComponent: () =>
      import('./pages/analyse-v7/analyse-v7.component').then((m) => m.AnalyseV7Component),
  },
  {
    path: 'analyse-v8',
    title: 'New Gap Analysis V8 · Comply Solution',
    loadComponent: () =>
      import('./pages/analyse-v8/analyse-v8.component').then((m) => m.AnalyseV8Component),
  },
  {
    path: 'gap-analysis',
    title: 'Gap Analysis · Comply Solution',
    loadComponent: () =>
      import('./pages/gap-analysis-report/gap-analysis-report.component').then(
        (m) => m.GapAnalysisReportComponent,
      ),
  },
  {
    path: 'regulations',
    title: 'Regulation Docs Library · Comply Solution',
    loadComponent: () =>
      import('./pages/regulation-library/regulation-library.component').then(
        (m) => m.RegulationLibraryComponent,
      ),
  },
  {
    path: 'documents',
    title: 'Document Library · Comply Solution',
    loadComponent: () =>
      import('./pages/documents/documents.component').then((m) => m.DocumentsComponent),
  },
  {
    path: 'in-progress',
    title: 'Analyses in progress · Comply Solution',
    loadComponent: () =>
      import('./pages/in-progress/in-progress.component').then((m) => m.InProgressComponent),
  },
  {
    path: 'dual-verify',
    title: 'Advanced Workbench · Comply Solution',
    loadComponent: () =>
      import('./pages/dual-verify/dual-verify.component').then((m) => m.DualVerifyComponent),
  },
];

/** Redirect bare legacy URLs to /old/* (preserves query params). */
const legacyRedirects: Routes = [
  { path: 'dashboard', redirectTo: 'old/dashboard', pathMatch: 'full' },
  { path: 'analyse', redirectTo: 'old/analyse', pathMatch: 'full' },
  { path: 'analyse-v2', redirectTo: 'old/analyse-v2', pathMatch: 'full' },
  { path: 'analyse-v3', redirectTo: 'old/analyse-v3', pathMatch: 'full' },
  { path: 'analyse-v4', redirectTo: 'old/analyse-v4', pathMatch: 'full' },
  { path: 'analyse-v5', redirectTo: 'old/analyse-v5', pathMatch: 'full' },
  { path: 'analyse-v6', redirectTo: 'old/analyse-v6', pathMatch: 'full' },
  { path: 'analyse-v7', redirectTo: 'old/analyse-v7', pathMatch: 'full' },
  { path: 'analyse-v8', redirectTo: 'old/analyse-v8', pathMatch: 'full' },
  { path: 'gap-analysis', redirectTo: 'old/gap-analysis', pathMatch: 'full' },
  { path: 'regulations', redirectTo: 'old/regulations', pathMatch: 'full' },
  { path: 'documents', redirectTo: 'old/documents', pathMatch: 'full' },
  { path: 'in-progress', redirectTo: 'old/in-progress', pathMatch: 'full' },
  { path: 'dual-verify', redirectTo: 'old/dual-verify', pathMatch: 'full' },
];

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'nd/overview' },
  { path: 'overview', redirectTo: 'nd/overview', pathMatch: 'full' },
  {
    path: 'login',
    title: 'Sign in · Comply Solution',
    canActivate: [guestGuard],
    loadComponent: () =>
      import('./pages/login/login.component').then((m) => m.LoginComponent),
  },
  ...legacyRedirects,
  {
    path: 'old',
    component: ShellComponent,
    canActivate: [authGuard],
    canActivateChild: [authGuard],
    children: legacyAppRoutes,
  },
  {
    path: 'nd',
    children: [
      {
        path: 'auth/login',
        title: 'Sign in · Comply Solution ND',
        canActivate: [ndGuestGuard],
        loadComponent: () =>
          import('./pages/nd/auth/nd-login.component').then((m) => m.NdLoginComponent),
      },
      {
        path: 'auth/forgot-password',
        title: 'Forgot password · Comply Solution ND',
        canActivate: [ndGuestGuard],
        loadComponent: () =>
          import('./pages/nd/auth/nd-forgot-password.component').then(
            (m) => m.NdForgotPasswordComponent,
          ),
      },
      {
        path: 'auth/reset-password',
        title: 'Reset password · Comply Solution ND',
        loadComponent: () =>
          import('./pages/nd/auth/nd-reset-password.component').then(
            (m) => m.NdResetPasswordComponent,
          ),
      },
      {
        path: 'auth/accept-invite',
        title: 'Accept invite · Comply Solution ND',
        loadComponent: () =>
          import('./pages/nd/auth/nd-accept-invite.component').then(
            (m) => m.NdAcceptInviteComponent,
          ),
      },
      {
        path: '',
        loadComponent: () =>
          import('./pages/nd/nd-shell.component').then((m) => m.NdShellComponent),
        canActivate: [ndAuthGuard],
        canActivateChild: [ndAuthGuard],
        children: [
          { path: '', redirectTo: 'overview', pathMatch: 'full' },
          {
            path: 'overview/metric',
            loadComponent: () =>
              import('./pages/nd/dashboard-metric-runs/nd-dashboard-metric-runs.component').then(
                (m) => m.NdDashboardMetricRunsComponent,
              ),
          },
          {
            path: 'overview',
            loadComponent: () =>
              import('./pages/nd/overview/nd-home.component').then((m) => m.NdHomeComponent),
          },
          {
            path: 'regulation-documents/deleted',
            canActivate: [ndRoleGuard],
            data: { ndRoles: ['super_admin'], deletedOnly: true },
            loadComponent: () =>
              import('./pages/nd/regulation-documents/nd-regulation-documents.component').then(
                (m) => m.NdRegulationDocumentsComponent,
              ),
          },
          {
            path: 'regulation-documents',
            loadComponent: () =>
              import('./pages/nd/regulation-documents/nd-regulation-documents.component').then(
                (m) => m.NdRegulationDocumentsComponent,
              ),
          },
          {
            path: 'regulations',
            redirectTo: 'regulation-documents',
            pathMatch: 'full',
          },
          {
            path: 'regulations/:docId',
            redirectTo: 'regulation-documents/:docId',
            pathMatch: 'full',
          },
          {
            path: 'regulation-documents/:docId',
            loadComponent: () =>
              import('./pages/nd/regulation-documents/nd-regulation-document-detail.component').then(
                (m) => m.NdRegulationDocumentDetailComponent,
              ),
          },
          {
            path: 'internal-documents/deleted',
            canActivate: [ndRoleGuard],
            data: { ndRoles: ['super_admin'], deletedOnly: true },
            loadComponent: () =>
              import('./pages/nd/internal-documents/nd-internal-documents.component').then(
                (m) => m.NdInternalDocumentsComponent,
              ),
          },
          {
            path: 'internal-documents',
            loadComponent: () =>
              import('./pages/nd/internal-documents/nd-internal-documents.component').then(
                (m) => m.NdInternalDocumentsComponent,
              ),
          },
          {
            // V2 — full clone of internal-documents above (same columns, filters, upload, sections
            // panel, analysis history, delete), but "Parse & Extract" runs locally (PdfPig +
            // Tesseract, no Landing AI). Separate route from internal-documents, which is untouched.
            path: 'internal-documents-new',
            data: { engine: 'tesseract' },
            loadComponent: () =>
              import('./pages/nd/internal-documents/nd-internal-documents-local.component').then(
                (m) => m.NdInternalDocumentsLocalComponent,
              ),
          },
          {
            // V2 — same as regulation-documents above, but local parse+extract instead of Landing AI.
            path: 'regulation-documents-new',
            data: { engine: 'tesseract' },
            loadComponent: () =>
              import('./pages/nd/regulation-documents/nd-regulation-documents-local.component').then(
                (m) => m.NdRegulationDocumentsLocalComponent,
              ),
          },
          {
            // Same page/component as internal-documents-new, but parse runs through RapidOCR (PaddleOCR
            // ONNX models) instead of Tesseract — a separate route so both can be compared side by side.
            path: 'internal-documents-rapidocr',
            data: { engine: 'rapidocr' },
            loadComponent: () =>
              import('./pages/nd/internal-documents/nd-internal-documents-local.component').then(
                (m) => m.NdInternalDocumentsLocalComponent,
              ),
          },
          {
            // Same page/component as regulation-documents-new, but parse runs through RapidOCR instead
            // of Tesseract — a separate route so both can be compared side by side.
            path: 'regulation-documents-rapidocr',
            data: { engine: 'rapidocr' },
            loadComponent: () =>
              import('./pages/nd/regulation-documents/nd-regulation-documents-local.component').then(
                (m) => m.NdRegulationDocumentsLocalComponent,
              ),
          },
          {
            // Same page/component as internal-documents-new, but parse runs through Docling's default
            // (light) pipeline — layout model + RapidOCR under the hood, via a local Python service.
            path: 'internal-documents-docling-light',
            data: { engine: 'docling-light' },
            loadComponent: () =>
              import('./pages/nd/internal-documents/nd-internal-documents-local.component').then(
                (m) => m.NdInternalDocumentsLocalComponent,
              ),
          },
          {
            path: 'regulation-documents-docling-light',
            data: { engine: 'docling-light' },
            loadComponent: () =>
              import('./pages/nd/regulation-documents/nd-regulation-documents-local.component').then(
                (m) => m.NdRegulationDocumentsLocalComponent,
              ),
          },
          {
            // Same page/component again, but Docling's VLM pipeline using GLM-OCR — far more accurate in
            // testing, but ~21 min/page on this CPU-only setup. Only practical for short documents until
            // this runs on a GPU. See docs/pipeline/LIBRARY-REFERENCE.md.
            path: 'internal-documents-docling-glm',
            data: { engine: 'docling-glm' },
            loadComponent: () =>
              import('./pages/nd/internal-documents/nd-internal-documents-local.component').then(
                (m) => m.NdInternalDocumentsLocalComponent,
              ),
          },
          {
            path: 'regulation-documents-docling-glm',
            data: { engine: 'docling-glm' },
            loadComponent: () =>
              import('./pages/nd/regulation-documents/nd-regulation-documents-local.component').then(
                (m) => m.NdRegulationDocumentsLocalComponent,
              ),
          },
          {
            // Third, simple document library — no department, no analysis-run linkage. Local
            // parse+extract, shown as a nested Point/Sub-point tree.
            path: 'text-documents',
            loadComponent: () =>
              import('./pages/nd/text-documents/nd-text-documents.component').then(
                (m) => m.NdTextDocumentsComponent,
              ),
          },
          {
            path: 'in-progress',
            loadComponent: () =>
              import('./pages/in-progress/in-progress.component').then((m) => m.InProgressComponent),
          },
          {
            path: 'inbox',
            title: 'My actions · Comply Solution',
            loadComponent: () =>
              import('./pages/nd/inbox/nd-inbox.component').then((m) => m.NdInboxComponent),
          },
          {
            path: 'profile',
            title: 'My profile · Comply Solution',
            loadComponent: () =>
              import('./pages/nd/profile/nd-profile.component').then((m) => m.NdProfileComponent),
          },
          {
            path: 'analyse-v8',
            ...ndAnalyseV8Route,
          },
          {
            path: 'analyse-v9',
            title: 'New Gap Analysis V2 · Comply Solution',
            ...ndAnalyseV9Route,
          },
          {
            path: 'analyse-regul',
            ...ndAnalyseRegulRoute,
          },
          {
            path: 'analyse-regul-full',
            ...ndAnalyseRegulFullRoute,
          },
          {
            path: 'analysis-versions',
            canActivate: [ndRoleGuard, ndDenyDemoViewerGuard],
            data: { ndRoles: ['maker', 'super_admin'] },
            title: 'Analysis versions · Comply Solution',
            loadComponent: () =>
              import('./pages/nd/analysis-versions/nd-analysis-versions.component').then(
                (m) => m.NdAnalysisVersionsComponent,
              ),
          },
          {
            path: 'dual-verify',
            loadComponent: () =>
              import('./pages/dual-verify/dual-verify.component').then((m) => m.DualVerifyComponent),
          },
          {
            path: 'gap-analysis',
            loadComponent: () =>
              import('./pages/nd/gap-analysis/nd-gap-analysis.component').then(
                (m) => m.NdGapAnalysisComponent,
              ),
          },
          {
            path: 'action-plans/:priority',
            title: 'Action plans by priority · Comply Solution',
            loadComponent: () =>
              import('./pages/nd/action-plans/nd-action-plan-priority.component').then(
                (m) => m.NdActionPlanPriorityComponent,
              ),
          },
          {
            path: 'libraries',
            loadComponent: () =>
              import('./pages/nd/libraries/nd-libraries.component').then((m) => m.NdLibrariesComponent),
          },
          {
            path: 'libraries/new',
            canActivate: [ndRoleGuard],
            data: { ndRoles: ['maker', 'super_admin'] },
            loadComponent: () =>
              import('./pages/nd/libraries/nd-library-builder.component').then(
                (m) => m.NdLibraryBuilderComponent,
              ),
          },
          {
            path: 'libraries/:libraryId/view',
            loadComponent: () =>
              import('./pages/nd/libraries/nd-library-view.component').then(
                (m) => m.NdLibraryViewComponent,
              ),
          },
          {
            path: 'libraries/:libraryId',
            canActivate: [ndRoleGuard],
            data: { ndRoles: ['maker', 'super_admin'] },
            loadComponent: () =>
              import('./pages/nd/libraries/nd-library-builder.component').then(
                (m) => m.NdLibraryBuilderComponent,
              ),
          },
          {
            path: 'analysis-runs',
            loadComponent: () =>
              import('./pages/nd/analysis-runs/nd-analysis-runs.component').then(
                (m) => m.NdAnalysisRunsComponent,
              ),
          },
          {
            path: 'run-analysis',
            redirectTo: 'analyse-regul-full',
            pathMatch: 'full',
          },
          {
            path: 'run-analysis/:runId',
            canActivate: [ndRoleGuard],
            data: { ndRoles: ['maker', 'super_admin'] },
            loadComponent: () =>
              import('./pages/nd/run-analysis/nd-run-analysis-progress.component').then(
                (m) => m.NdRunAnalysisProgressComponent,
              ),
          },
          {
            path: 'results/:runId',
            loadComponent: () =>
              import('./pages/nd/results/nd-results.component').then((m) => m.NdResultsComponent),
          },
          {
            path: 'checker',
            canActivate: [ndRoleGuard],
            data: { ndRoles: ['checker', 'reviewer', 'super_admin'] },
            loadComponent: () =>
              import('./pages/nd/checker/nd-checker-queue.component').then(
                (m) => m.NdCheckerQueueComponent,
              ),
          },
          {
            path: 'checker/review/:runId',
            canActivate: [ndRoleGuard],
            data: { ndRoles: ['checker', 'super_admin'] },
            loadComponent: () =>
              import('./pages/nd/checker/nd-checker-review.component').then(
                (m) => m.NdCheckerReviewComponent,
              ),
          },
          {
            path: 'correction/review',
            pathMatch: 'full',
            redirectTo: (snap) => {
              const runId = snap.queryParams['run'];
              return runId ? `correction/review/${runId}` : 'analysis-runs';
            },
          },
          {
            path: 'correction/review/:runId',
            canActivate: [ndRoleGuard],
            data: { ndRoles: ['maker', 'checker', 'reviewer', 'super_admin'] },
            loadComponent: () =>
              import('./pages/nd/correction/nd-correction-review.component').then(
                (m) => m.NdCorrectionReviewComponent,
              ),
          },
          {
            path: 'reviewer',
            canActivate: [ndRoleGuard],
            data: { ndRoles: ['reviewer', 'super_admin'] },
            loadComponent: () =>
              import('./pages/nd/reviewer/nd-reviewer-queue.component').then(
                (m) => m.NdReviewerQueueComponent,
              ),
          },
          {
            path: 'reviewer/review/:runId',
            canActivate: [ndRoleGuard],
            data: { ndRoles: ['reviewer', 'super_admin'] },
            loadComponent: () =>
              import('./pages/nd/reviewer/nd-reviewer-review.component').then(
                (m) => m.NdReviewerReviewComponent,
              ),
          },
          {
            path: 'admin/users',
            canActivate: [ndRoleGuard],
            data: { ndRoles: ['super_admin'] },
            loadComponent: () =>
              import('./pages/nd/admin/nd-admin-users.component').then((m) => m.NdAdminUsersComponent),
          },
          {
            path: 'admin/departments',
            canActivate: [ndRoleGuard],
            data: { ndRoles: ['super_admin'] },
            loadComponent: () =>
              import('./pages/nd/admin/nd-admin-departments.component').then(
                (m) => m.NdAdminDepartmentsComponent,
              ),
          },
          {
            path: 'admin/deleted-runs',
            canActivate: [ndRoleGuard],
            data: { ndRoles: ['super_admin'] },
            loadComponent: () =>
              import('./pages/nd/admin/nd-admin-deleted-runs.component').then(
                (m) => m.NdAdminDeletedRunsComponent,
              ),
          },
          {
            path: 'admin/settings',
            canActivate: [ndRoleGuard],
            data: { ndRoles: ['super_admin'] },
            loadComponent: () =>
              import('./pages/nd/admin/nd-admin-settings.component').then(
                (m) => m.NdAdminSettingsComponent,
              ),
          },
          {
            path: 'admin/demo',
            canActivate: [ndRoleGuard, ndDenyDemoViewerGuard],
            data: { ndRoles: ['super_admin'] },
            title: 'Demo group · Comply Solution',
            loadComponent: () =>
              import('./pages/nd/admin/nd-admin-demo.component').then((m) => m.NdAdminDemoComponent),
          },
          {
            path: 'admin/prompts',
            canActivate: [ndRoleGuard],
            data: { ndRoles: ['super_admin'] },
            loadComponent: () =>
              import('./pages/nd/admin/nd-admin-prompts.component').then(
                (m) => m.NdAdminPromptsComponent,
              ),
          },
        ],
      },
    ],
  },
  { path: 'run-analysis', redirectTo: 'nd/analyse-regul-full', pathMatch: 'full' },
  { path: '**', redirectTo: 'nd/overview' },
];

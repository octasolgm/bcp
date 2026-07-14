import { Routes } from '@angular/router';
import { ShellComponent } from './layout/shell.component';
import { authGuard, guestGuard } from './guards/auth.guard';

export const routes: Routes = [
  {
    path: 'login',
    title: 'Sign in · Reguliq',
    canActivate: [guestGuard],
    loadComponent: () =>
      import('./pages/login/login.component').then((m) => m.LoginComponent),
  },
  {
    path: '',
    component: ShellComponent,
    canActivate: [authGuard],
    canActivateChild: [authGuard],
    children: [
      { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
      {
        path: 'dashboard',
        title: 'Dashboard · Reguliq',
        loadComponent: () =>
          import('./pages/dashboard/dashboard.component').then((m) => m.DashboardComponent),
      },
      {
        path: 'analyse',
        title: 'New Gap Analysis · Reguliq',
        loadComponent: () =>
          import('./pages/analyse/analyse.component').then((m) => m.AnalyseComponent),
      },
      {
        path: 'analyse-v2',
        title: 'New Gap Analysis V2 · Reguliq',
        loadComponent: () =>
          import('./pages/analyse-v2/analyse-v2.component').then((m) => m.AnalyseV2Component),
      },
      {
        path: 'analyse-v3',
        title: 'New Gap Analysis V3 · Reguliq',
        loadComponent: () =>
          import('./pages/analyse-v3/analyse-v3.component').then((m) => m.AnalyseV3Component),
      },
      {
        path: 'analyse-v4',
        title: 'New Gap Analysis V4 · Reguliq',
        loadComponent: () =>
          import('./pages/analyse-v4/analyse-v4.component').then((m) => m.AnalyseV4Component),
      },
      {
        path: 'analyse-v5',
        title: 'New Gap Analysis V5 · Reguliq',
        loadComponent: () =>
          import('./pages/analyse-v5/analyse-v5.component').then((m) => m.AnalyseV5Component),
      },
      {
        path: 'analyse-v6',
        title: 'New Gap Analysis V6 · Reguliq',
        loadComponent: () =>
          import('./pages/analyse-v6/analyse-v6.component').then((m) => m.AnalyseV6Component),
      },
      {
        path: 'analyse-v7',
        title: 'New Gap Analysis V7 · Reguliq',
        loadComponent: () =>
          import('./pages/analyse-v7/analyse-v7.component').then((m) => m.AnalyseV7Component),
      },
      {
        path: 'analyse-v8',
        title: 'New Gap Analysis V8 · Reguliq',
        loadComponent: () =>
          import('./pages/analyse-v8/analyse-v8.component').then((m) => m.AnalyseV8Component),
      },
      {
        path: 'gap-analysis',
        title: 'Gap Analysis · Reguliq',
        loadComponent: () =>
          import('./pages/gap-analysis-report/gap-analysis-report.component').then(
            (m) => m.GapAnalysisReportComponent,
          ),
      },
      {
        path: 'regulations',
        title: 'Regulation Library · Reguliq',
        loadComponent: () =>
          import('./pages/regulation-library/regulation-library.component').then(
            (m) => m.RegulationLibraryComponent,
          ),
      },
      {
        path: 'documents',
        title: 'Document Library · Reguliq',
        loadComponent: () =>
          import('./pages/documents/documents.component').then((m) => m.DocumentsComponent),
      },
      {
        path: 'in-progress',
        title: 'Analyses in progress · Reguliq',
        loadComponent: () =>
          import('./pages/in-progress/in-progress.component').then((m) => m.InProgressComponent),
      },
      {
        path: 'dual-verify',
        title: 'Advanced Workbench · Reguliq',
        loadComponent: () =>
          import('./pages/dual-verify/dual-verify.component').then((m) => m.DualVerifyComponent),
      },
    ],
  },
  { path: '**', redirectTo: 'dashboard' },
];

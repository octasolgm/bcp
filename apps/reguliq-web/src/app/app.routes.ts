import { Routes } from '@angular/router';
import { ShellComponent } from './layout/shell.component';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
      {
        path: 'dashboard',
        title: 'Dashboard · BCP App',
        loadComponent: () =>
          import('./pages/dashboard/dashboard.component').then((m) => m.DashboardComponent),
      },
      {
        path: 'dual-verify',
        title: 'Dual Verify · BCP App',
        loadComponent: () =>
          import('./pages/dual-verify/dual-verify.component').then((m) => m.DualVerifyComponent),
      },
    ],
  },
];

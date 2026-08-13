import { Component, OnInit, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { ND_NEW_ANALYSIS_PATH } from '../../../../lib/nd/demo-analysis-routes';

export type AnalysisVersionEntry = {
  id: string;
  label: string;
  description: string;
  path: string | null;
  status: 'available' | 'coming_soon';
};

@Component({
  selector: 'app-nd-analysis-versions',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './nd-analysis-versions.component.html',
  styleUrls: ['./nd-analysis-versions.component.scss', '../nd-shared.scss'],
})
export class NdAnalysisVersionsComponent implements OnInit {
  private readonly router = inject(Router);
  private readonly auth = inject(NdAuthService);

  ngOnInit(): void {
    if (this.auth.isDemoViewer()) {
      void this.router.navigateByUrl(ND_NEW_ANALYSIS_PATH, { replaceUrl: true });
    }
  }

  readonly versions: AnalysisVersionEntry[] = [
    {
      id: 'V1',
      label: 'Analysis V1',
      description: 'Original analysis page (analyse v8). Production UI — leave unchanged.',
      path: '/nd/analyse-v8',
      status: 'available',
    },
    {
      id: 'V2',
      label: 'Analysis V2',
      description: 'Cloned analysis page for experiments. Safe to modify without touching V1.',
      path: '/nd/analyse-v9',
      status: 'available',
    },
    {
      id: 'V3',
      label: 'Analysis V3 — Regul Workflow',
      description:
        'V8 setup (reg library + points + internal docs) then Regul.ai forward/reverse/qualitative pipeline with admin-selected LLM.',
      path: '/nd/analyse-regul',
      status: 'available',
    },
    {
      id: 'V4',
      label: 'Analysis V4 — Regul Full Markdown',
      description:
        'Clone of V3 with full internal markdown sent to the LLM (all files, no top-20 retrieval), forward-only (no reverse), and prompt caching when supported.',
      path: '/nd/analyse-regul-full',
      status: 'available',
    },
  ];
}

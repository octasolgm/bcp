import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

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
export class NdAnalysisVersionsComponent {
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
      label: 'Analysis V3',
      description: 'Reserved for a future clone. Not available yet.',
      path: null,
      status: 'coming_soon',
    },
  ];
}

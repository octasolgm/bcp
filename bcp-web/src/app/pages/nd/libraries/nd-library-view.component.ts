import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { NdLibraryPointsPanelComponent } from './nd-library-points-panel.component';

@Component({
  selector: 'app-nd-library-view',
  standalone: true,
  imports: [CommonModule, RouterLink, NdLibraryPointsPanelComponent],
  templateUrl: './nd-library-view.component.html',
  styleUrls: ['./nd-library-view.component.scss', '../nd-shared.scss'],
})
export class NdLibraryViewComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly auth = inject(NdAuthService);

  libraryId = '';
  name = '';
  description = '';

  get canEdit(): boolean {
    const role = this.auth.getRole();
    return role === 'maker' || role === 'super_admin';
  }

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    const id = this.route.snapshot.paramMap.get('libraryId');
    if (!id) {
      void this.router.navigate(['/nd/libraries']);
      return;
    }
    void this.router.navigate(['/nd/libraries'], {
      queryParams: { view: id },
      replaceUrl: true,
    });
  }
}

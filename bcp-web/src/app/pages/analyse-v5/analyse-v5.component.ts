import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { InProgressNavButtonComponent } from '../../components/in-progress-nav-button/in-progress-nav-button.component';
import { AnalyseBase } from '../shared/analyse-base';

@Component({
  selector: 'app-analyse-v5',
  standalone: true,
  imports: [CommonModule, FormsModule, InProgressNavButtonComponent],
  templateUrl: './analyse-v5.component.html',
  styleUrl: './analyse-v5.component.scss',
})
export class AnalyseV5Component extends AnalyseBase {
  readonly versionLabel = 'V5 — Left Rail';
  readonly versionPath = '/analyse-v5';
}

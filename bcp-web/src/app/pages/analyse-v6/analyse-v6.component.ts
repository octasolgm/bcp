import { Component } from '@angular/core';
import { CommonModule, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { InProgressNavButtonComponent } from '../../components/in-progress-nav-button/in-progress-nav-button.component';
import { AnalyseBase } from '../shared/analyse-base';

@Component({
  selector: 'app-analyse-v6',
  standalone: true,
  imports: [CommonModule, FormsModule, DecimalPipe, InProgressNavButtonComponent],
  templateUrl: './analyse-v6.component.html',
  styleUrl: './analyse-v6.component.scss',
})
export class AnalyseV6Component extends AnalyseBase {
  readonly versionLabel = 'V6 — Table View';
  readonly versionPath = '/analyse-v6';
}

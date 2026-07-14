import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { InProgressNavButtonComponent } from '../../components/in-progress-nav-button/in-progress-nav-button.component';
import { AnalyseBase } from '../shared/analyse-base';

@Component({
  selector: 'app-analyse-v4',
  standalone: true,
  imports: [CommonModule, FormsModule, InProgressNavButtonComponent],
  templateUrl: './analyse-v4.component.html',
  styleUrl: './analyse-v4.component.scss',
})
export class AnalyseV4Component extends AnalyseBase {
  readonly versionLabel = 'V4 — Document Strip';
  readonly versionPath = '/analyse-v4';
}

import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { InProgressNavButtonComponent } from '../../components/in-progress-nav-button/in-progress-nav-button.component';
import { AnalyseBase } from '../shared/analyse-base';

@Component({
  selector: 'app-analyse-v7',
  standalone: true,
  imports: [CommonModule, FormsModule, InProgressNavButtonComponent],
  templateUrl: './analyse-v7.component.html',
  styleUrl: './analyse-v7.component.scss',
})
export class AnalyseV7Component extends AnalyseBase {
  readonly versionLabel = 'V7 — Split Screen';
  readonly versionPath = '/analyse-v7';
}

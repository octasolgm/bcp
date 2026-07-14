import { Injectable, signal } from '@angular/core';

export type ToastKind = 'info' | 'success' | 'warning' | 'error';

export type ToastMessage = {
  id: number;
  kind: ToastKind;
  text: string;
};

@Injectable({ providedIn: 'root' })
export class ToastService {
  private nextId = 1;
  readonly messages = signal<ToastMessage[]>([]);

  show(text: string, kind: ToastKind = 'info', ms = 3200): void {
    const id = this.nextId++;
    this.messages.update((list) => [...list, { id, kind, text }]);
    window.setTimeout(() => this.dismiss(id), ms);
  }

  dismiss(id: number): void {
    this.messages.update((list) => list.filter((m) => m.id !== id));
  }
}

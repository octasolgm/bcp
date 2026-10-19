import { Component, HostListener, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import {
  NdApiService,
  type NdCloudImportLogRow,
  type NdCloudProvider,
} from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { ToastService } from '../../../services/toast.service';

/** Services we plan to add behind the same Connect button. Shown so the list of platforms is visible. */
const COMING_SOON: { id: string; name: string }[] = [
  { id: 'microsoft', name: 'OneDrive and SharePoint' },
  { id: 'dropbox', name: 'Dropbox' },
  { id: 'box', name: 'Box' },
  { id: 'pcloud', name: 'pCloud' },
];

/**
 * A workspace admin connects the workspace's own cloud storage here (Google Drive now), through the provider's
 * sign-in pop-up, and can see what has been imported. The platform owner also sees the address to register in each
 * provider's console. See docs/storage-provider-integration.
 */
@Component({
  selector: 'app-nd-admin-integrations',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './nd-admin-integrations.component.html',
  styleUrls: ['./nd-admin-integrations.component.scss', './nd-admin-modern.scss', '../nd-shared.scss'],
})
export class NdAdminIntegrationsComponent implements OnInit, OnDestroy {
  private readonly api = inject(NdApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  readonly auth = inject(NdAuthService);

  providers: NdCloudProvider[] = [];
  log: NdCloudImportLogRow[] = [];
  loading = true;
  error = '';
  busyId: string | null = null;
  private popupWatch: ReturnType<typeof setInterval> | null = null;

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    // Back from a sign-in that opened in this tab (pop-up blocked): the server added a result to the address.
    const q = this.route.snapshot.queryParamMap;
    if (q.get('connected')) this.toast.show('Connected.', 'success', 4000);
    else if (q.get('error')) this.toast.show(q.get('error') ?? 'Not connected', 'error', 6000);
    if (q.get('connected') || q.get('error')) {
      void this.router.navigate([], { relativeTo: this.route, queryParams: {}, replaceUrl: true });
    }
    await this.load();
  }

  ngOnDestroy(): void {
    if (this.popupWatch) clearInterval(this.popupWatch);
  }

  get comingSoon(): { id: string; name: string }[] {
    const have = new Set(this.providers.map((p) => p.id));
    return COMING_SOON.filter((c) => !have.has(c.id));
  }

  get isPlatformAdmin(): boolean {
    return this.auth.isPlatformAdmin();
  }

  async load(): Promise<void> {
    this.loading = true;
    const [prov, log] = await Promise.all([this.api.getCloudProviders(), this.api.getCloudImportLog(30)]);
    if (prov.success && prov.data) this.providers = prov.data;
    else this.error = prov.message ?? 'Could not load integrations.';
    this.log = log.success && log.data ? log.data : [];
    this.loading = false;
  }

  /** The sign-in pop-up tells this page how it went; only messages from our own API are trusted. */
  @HostListener('window:message', ['$event'])
  onMessage(event: MessageEvent): void {
    if (event.origin !== this.api.cloudApiOrigin()) return;
    const data = event.data as { type?: string; ok?: boolean; message?: string } | null;
    if (data?.type !== 'cloud-connect') return;
    this.finishConnect();
    this.toast.show(data.message ?? (data.ok ? 'Connected.' : 'Not connected.'), data.ok ? 'success' : 'error', 6000);
    void this.load();
  }

  private finishConnect(): void {
    this.busyId = null;
    if (this.popupWatch) {
      clearInterval(this.popupWatch);
      this.popupWatch = null;
    }
  }

  async connect(provider: NdCloudProvider): Promise<void> {
    if (this.busyId) return;
    this.busyId = provider.id;
    const res = await this.api.startCloudConnect(provider.id);
    if (!res.success || !res.data) {
      this.busyId = null;
      this.toast.show(res.message ?? 'Could not start the connection.', 'error', 6000);
      return;
    }
    const popup = window.open(res.data.url, 'cloud-connect', 'popup=yes,width=520,height=700');
    if (!popup) {
      // Pop-ups are blocked: sign in in this tab; the server brings the browser back here.
      window.location.href = res.data.url;
      return;
    }
    // Closed without finishing: stop waiting.
    this.popupWatch = setInterval(() => {
      if (popup.closed) {
        this.finishConnect();
        void this.load();
      }
    }, 600);
  }

  async disconnect(provider: NdCloudProvider): Promise<void> {
    const connection = provider.connection;
    if (!connection || this.busyId) return;
    const ok = window.confirm(
      `Disconnect ${provider.name}? Documents you already imported stay. Importing again will need a new connection.`,
    );
    if (!ok) return;
    this.busyId = provider.id;
    const res = await this.api.disconnectCloud(connection.id);
    this.busyId = null;
    if (res.success) {
      this.toast.show(`${provider.name} disconnected.`, 'success', 4000);
      await this.load();
    } else {
      this.toast.show(res.message ?? 'Could not disconnect.', 'error', 6000);
    }
  }

  async copy(text: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
      this.toast.show('Copied.', 'success', 2000);
    } catch {
      this.toast.show('Could not copy. Select the address and copy it by hand.', 'warning', 4000);
    }
  }

  statusLabel(p: NdCloudProvider): string {
    if (!p.configured) return 'Not set up yet';
    if (!p.connection) return 'Not connected';
    return p.connection.status === 'needs_reconnect' ? 'Needs reconnect' : 'Connected';
  }

  statusKind(p: NdCloudProvider): 'ok' | 'warn' | 'off' {
    if (!p.configured || !p.connection) return 'off';
    return p.connection.status === 'needs_reconnect' ? 'warn' : 'ok';
  }

  kindLabel(kind: string): string {
    return kind === 'regulation' ? 'Regulation' : 'Internal';
  }

  resultLabel(row: NdCloudImportLogRow): string {
    if (row.result === 'imported') return 'Imported';
    if (row.result === 'duplicate') return 'Already there';
    return row.error ? `Failed: ${row.error}` : 'Failed';
  }
}

import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { saveAs } from 'file-saver-es';
import { Torrent } from '../models/torrent.model';
import { TorrentService } from '../torrent.service';
import { NgClass, DatePipe } from '@angular/common';
import { CdkCopyToClipboard } from '@angular/cdk/clipboard';
import { FormsModule } from '@angular/forms';
import { TorrentStatusPipe } from '../torrent-status.pipe';
import { DownloadStatusPipe } from '../download-status.pipe';
import { DecodeURIPipe } from '../decode-uri.pipe';
import { FileSizePipe } from '../filesize.pipe';
import { switchMap } from 'rxjs';

@Component({
  selector: 'app-torrent',
  host: { class: 'page-layout' },
  templateUrl: './torrent.component.html',
  styleUrls: ['./torrent.component.scss'],
  imports: [
    NgClass,
    CdkCopyToClipboard,
    FormsModule,
    DatePipe,
    TorrentStatusPipe,
    DownloadStatusPipe,
    DecodeURIPipe,
    FileSizePipe,
  ],
  standalone: true,
})
export class TorrentComponent implements OnInit {
  private activatedRoute = inject(ActivatedRoute);
  private router = inject(Router);
  private torrentService = inject(TorrentService);

  public readonly torrentState = signal<Torrent | null>(null);

  public activeTab: number = 0;

  public copied: boolean = false;

  public downloadExpanded: { [downloadId: string]: boolean } = {};

  public readonly isDeleteModalActive = signal(false);
  public readonly deleteError = signal<string | null>(null);
  public readonly deleting = signal(false);
  public deleteSelectAll: boolean;
  public deleteData: boolean;
  public deleteRdTorrent: boolean;
  public deleteLocalFiles: boolean;

  public readonly isRetryModalActive = signal(false);
  public readonly retryError = signal<string | null>(null);
  public readonly retrying = signal(false);

  public readonly isDownloadRetryModalActive = signal(false);
  public readonly downloadRetryError = signal<string | null>(null);
  public readonly downloadRetrying = signal(false);
  public downloadRetryId: string;

  public readonly isUpdateSettingsModalActive = signal(false);
  public readonly updateSettingsError = signal<string | null>(null);

  public updateSettingsDownloadClient: number;
  public updateSettingsHostDownloadAction: number;
  public updateSettingsCategory: string;
  public updateSettingsPriority: number;
  public updateSettingsDownloadRetryAttempts: number;
  public updateSettingsTorrentRetryAttempts: number;
  public updateSettingsDeleteOnError: number;
  public updateSettingsTorrentLifetime: number;

  public readonly updating = signal(false);

  private readonly destroyRef = inject(DestroyRef);

  ngOnInit(): void {
    this.activatedRoute.paramMap
      .pipe(
        switchMap((params) => this.torrentService.get(params.get('id'))),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe({
        next: (torrent) => this.torrentState.set(torrent),
        error: () => this.router.navigate(['/torrents']),
      });

    this.torrentService.update$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((result) => this.update(result));
  }

  public update(torrents: Torrent[]): void {
    const torrent = this.torrentState();

    if (torrent === null) {
      return;
    }

    const updatedTorrent = torrents.find((item) => item.torrentId === torrent.torrentId);

    if (updatedTorrent !== undefined) {
      this.torrentState.set(updatedTorrent);
    }
  }

  public download(): void {
    const torrent = this.torrentState();

    if (torrent === null) {
      return;
    }

    const byteArray = new Uint8Array(
      window
        .atob(torrent.fileOrMagnet)
        .split('')
        .map(function (c) {
          return c.charCodeAt(0);
        })
    );

    const blob = new Blob([byteArray], { type: 'application/x-bittorrent' });
    saveAs(blob, `${torrent.rdName}.torrent`);
  }

  public showDeleteModal(): void {
    this.deleteSelectAll = false;
    this.deleteData = false;
    this.deleteRdTorrent = false;
    this.deleteLocalFiles = false;
    this.deleteError.set(null);

    this.isDeleteModalActive.set(true);
  }

  public deleteCancel(): void {
    this.isDeleteModalActive.set(false);
  }

  public deleteOk(): void {
    const torrent = this.torrentState();

    if (torrent === null) {
      return;
    }

    if (!this.hasDeleteAction()) {
      this.deleteError.set('Select at least one delete action.');
      return;
    }

    this.deleting.set(true);

    this.torrentService
      .delete(torrent.torrentId, this.deleteData, this.deleteRdTorrent, this.deleteLocalFiles)
      .subscribe({
        next: () => {
          this.isDeleteModalActive.set(false);
          this.deleting.set(false);

          this.router.navigate(['/torrents']);
        },
        error: (err) => {
          this.deleteError.set(err.error);
          this.deleting.set(false);
        },
      });
  }

  public showRetryModal(): void {
    this.retryError.set(null);

    this.isRetryModalActive.set(true);
  }

  public retryCancel(): void {
    this.isRetryModalActive.set(false);
  }

  public retryOk(): void {
    const torrent = this.torrentState();

    if (torrent === null) {
      return;
    }

    this.retrying.set(true);

    this.torrentService.retry(torrent.torrentId).subscribe({
      next: () => {
        this.isRetryModalActive.set(false);
        this.retrying.set(false);

        this.router.navigate(['/torrents']);
      },
      error: (err) => {
        this.retryError.set(err.error);
        this.retrying.set(false);
      },
    });
  }

  public showDownloadRetryModal(downloadId: string): void {
    this.downloadRetryId = downloadId;
    this.downloadRetryError.set(null);

    this.isDownloadRetryModalActive.set(true);
  }

  public downloadRetryCancel(): void {
    this.isDownloadRetryModalActive.set(false);
  }

  public downloadRetryOk(): void {
    this.downloadRetrying.set(true);

    this.torrentService.retryDownload(this.downloadRetryId).subscribe({
      next: () => {
        this.isDownloadRetryModalActive.set(false);
        this.downloadRetrying.set(false);
      },
      error: (err) => {
        this.downloadRetryError.set(err.error);
        this.downloadRetrying.set(false);
      },
    });
  }

  public showUpdateSettingsModal(): void {
    const torrent = this.torrentState();

    if (torrent === null) {
      return;
    }

    this.updateSettingsDownloadClient = torrent.downloadClient;
    this.updateSettingsHostDownloadAction = torrent.hostDownloadAction;
    this.updateSettingsCategory = torrent.category;
    this.updateSettingsPriority = torrent.priority;
    this.updateSettingsDownloadRetryAttempts = torrent.downloadRetryAttempts;
    this.updateSettingsTorrentRetryAttempts = torrent.torrentRetryAttempts;
    this.updateSettingsDeleteOnError = torrent.deleteOnError;
    this.updateSettingsTorrentLifetime = torrent.lifetime;

    this.updateSettingsError.set(null);
    this.isUpdateSettingsModalActive.set(true);
  }

  public updateSettingsCancel(): void {
    if (!this.updating()) {
      this.isUpdateSettingsModalActive.set(false);
    }
  }

  public updateSettingsOk(): void {
    const torrent = this.torrentState();

    if (torrent === null || this.updating()) {
      return;
    }

    this.updateSettingsError.set(null);
    this.updating.set(true);

    const settings = {
      downloadClient: this.updateSettingsDownloadClient,
      hostDownloadAction: this.updateSettingsHostDownloadAction,
      category: this.updateSettingsCategory,
      priority: this.updateSettingsPriority,
      downloadRetryAttempts: this.updateSettingsDownloadRetryAttempts,
      torrentRetryAttempts: this.updateSettingsTorrentRetryAttempts,
      deleteOnError: this.updateSettingsDeleteOnError,
      lifetime: this.updateSettingsTorrentLifetime,
    };

    this.torrentService.update({ ...torrent, ...settings }).subscribe({
      next: () => {
        this.torrentState.update((current) =>
          current?.torrentId === torrent.torrentId ? { ...current, ...settings } : current
        );
        this.isUpdateSettingsModalActive.set(false);
        this.updating.set(false);
      },
      error: (err) => {
        this.updateSettingsError.set(
          typeof err.error === 'string' && err.error.trim()
            ? err.error.trim()
            : 'Torrent settings could not be saved. Please try again.'
        );
        this.updating.set(false);
      },
    });
  }
  toggleDeleteSelectAllOptions() {
    this.deleteData = this.deleteSelectAll;
    this.deleteRdTorrent = this.deleteSelectAll;
    this.deleteLocalFiles = this.deleteSelectAll;
  }

  updateDeleteSelectAll() {
    this.deleteSelectAll = this.deleteData && this.deleteRdTorrent && this.deleteLocalFiles;
  }

  public hasDeleteAction(): boolean {
    return this.deleteData || this.deleteRdTorrent || this.deleteLocalFiles;
  }
}

import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Torrent } from '../models/torrent.model';
import { TorrentService } from '../torrent.service';
import { forkJoin, Observable } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { NgClass, DecimalPipe, DatePipe } from '@angular/common';
import { TorrentStatusPipe } from '../torrent-status.pipe';
import { FileSizePipe } from '../filesize.pipe';
import {
  pruneTorrentSelection,
  selectVisibleTorrents,
  torrentColumns,
  visibleSelectionState,
  visibleTorrents,
  type SortDirection,
  type TorrentSortKey,
} from './torrent-table-state';

@Component({
  selector: 'app-torrent-table',
  templateUrl: './torrent-table.component.html',
  styleUrls: ['./torrent-table.component.scss'],
  imports: [FormsModule, NgClass, DecimalPipe, DatePipe, TorrentStatusPipe, FileSizePipe, RouterLink],
  standalone: true,
})
export class TorrentTableComponent implements OnInit {
  private torrentService = inject(TorrentService);

  public readonly torrents = signal<Torrent[]>([]);
  public readonly selectedTorrents = signal<string[]>([]);
  public readonly error = signal<string | null>(null);
  public readonly sortColumns = torrentColumns;
  public readonly sortProperty = signal<TorrentSortKey>('rdName');
  public readonly sortDirection = signal<SortDirection>('asc');
  public readonly filterText = signal('');
  public readonly visibleTorrents = computed(() =>
    visibleTorrents(this.torrents(), this.filterText(), this.sortProperty(), this.sortDirection())
  );
  private readonly visibleSelection = computed(() =>
    visibleSelectionState(this.selectedTorrents(), this.visibleTorrents())
  );
  public readonly allVisibleSelected = computed(() => this.visibleSelection().all);
  public readonly someVisibleSelected = computed(() => this.visibleSelection().some);

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

  public readonly isChangeSettingsModalActive = signal(false);
  public readonly changeSettingsError = signal<string | null>(null);
  public readonly changingSettings = signal(false);

  public updateSettingsDownloadClient: number;
  public updateSettingsHostDownloadAction: number;
  public updateSettingsCategory: string;
  public updateSettingsPriority: number;
  public updateSettingsDownloadRetryAttempts: number;
  public updateSettingsTorrentRetryAttempts: number;
  public updateSettingsDeleteOnError: number;
  public updateSettingsTorrentLifetime: number;

  constructor() {
    this.torrentService.update$.pipe(takeUntilDestroyed()).subscribe((result) => {
      this.setTorrents(result);
    });
  }

  ngOnInit(): void {
    this.torrentService.getList().subscribe({
      next: (result) => {
        this.setTorrents(result);
      },
      error: (err) => {
        this.error.set(err.error);
      },
    });
  }

  private setTorrents(torrents: Torrent[]): void {
    this.torrents.set(torrents);
    this.selectedTorrents.update((selectedIds) => pruneTorrentSelection(selectedIds, torrents));
  }

  public sort(property: TorrentSortKey): void {
    this.sortDirection.update((direction) =>
      this.sortProperty() === property && direction === 'asc' ? 'desc' : 'asc'
    );
    this.sortProperty.set(property);
  }

  public sortIcon(property: TorrentSortKey): Record<string, boolean> {
    const active = this.sortProperty() === property;
    return {
      'fa-sort': !active,
      'fa-sort-up': active && this.sortDirection() === 'asc',
      'fa-sort-down': active && this.sortDirection() === 'desc',
      'sort-active': active,
    };
  }

  public sortAria(property: TorrentSortKey): 'ascending' | 'descending' | 'none' {
    return this.sortProperty() === property ? (this.sortDirection() === 'asc' ? 'ascending' : 'descending') : 'none';
  }

  public toggleSelectVisible(event: Event): void {
    const selected = (event.target as HTMLInputElement).checked;
    this.selectedTorrents.update((selectedIds) => selectVisibleTorrents(selectedIds, this.visibleTorrents(), selected));
  }

  public toggleSelect(torrentId: string) {
    this.selectedTorrents.update((selectedTorrents) =>
      selectedTorrents.includes(torrentId)
        ? selectedTorrents.filter((selectedTorrentId) => selectedTorrentId !== torrentId)
        : [...selectedTorrents, torrentId]
    );
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
    if (!this.hasDeleteAction()) {
      this.deleteError.set('Select at least one delete action.');
      return;
    }

    this.deleting.set(true);

    const calls: Observable<void>[] = [];

    this.selectedTorrents().forEach((torrentId) => {
      calls.push(this.torrentService.delete(torrentId, this.deleteData, this.deleteRdTorrent, this.deleteLocalFiles));
    });

    forkJoin(calls).subscribe({
      complete: () => {
        this.isDeleteModalActive.set(false);
        this.deleting.set(false);

        this.selectedTorrents.set([]);
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
    this.retrying.set(true);

    const calls: Observable<void>[] = [];

    this.selectedTorrents().forEach((torrentId) => {
      calls.push(this.torrentService.retry(torrentId));
    });

    forkJoin(calls).subscribe({
      complete: () => {
        this.isRetryModalActive.set(false);
        this.retrying.set(false);

        this.selectedTorrents.set([]);
      },
      error: (err) => {
        this.retryError.set(err.error);
        this.retrying.set(false);
      },
    });
  }

  public changeSettingsModal(): void {
    this.changeSettingsError.set(null);

    const selectedTorrents = this.selectedTorrents();
    const selected = this.torrents().filter((torrent) => selectedTorrents.includes(torrent.torrentId));
    const cv = <V>(getter: (t: Torrent) => V) => this.consensus(selected, getter);

    this.updateSettingsDownloadClient = cv((m) => m.downloadClient);
    this.updateSettingsHostDownloadAction = cv((m) => m.hostDownloadAction);
    this.updateSettingsCategory = cv((m) => m.category);
    this.updateSettingsPriority = cv((m) => m.priority);
    this.updateSettingsDownloadRetryAttempts = cv((m) => m.downloadRetryAttempts);
    this.updateSettingsTorrentRetryAttempts = cv((m) => m.torrentRetryAttempts);
    this.updateSettingsDeleteOnError = cv((m) => m.deleteOnError);
    this.updateSettingsTorrentLifetime = cv((m) => m.lifetime);

    this.isChangeSettingsModalActive.set(true);
  }

  private consensus<V>(items: Torrent[], getter: (item: Torrent) => V): V | null {
    if (items.length === 0) {
      return null;
    }
    const first = getter(items[0]);
    return items.every((item) => getter(item) === first) ? first : null;
  }

  public changeSettingsCancel(): void {
    this.isChangeSettingsModalActive.set(false);
  }

  public changeSettingsOk(): void {
    this.changingSettings.set(true);

    const calls: Observable<void>[] = [];

    const selectedTorrentIds = this.selectedTorrents();
    const selectedTorrents = this.torrents().filter((torrent) => selectedTorrentIds.includes(torrent.torrentId));

    selectedTorrents.forEach((currentTorrent) => {
      const torrent = { ...currentTorrent };
      if (this.updateSettingsDownloadClient != null) {
        torrent.downloadClient = this.updateSettingsDownloadClient;
      }
      if (this.updateSettingsHostDownloadAction != null) {
        torrent.hostDownloadAction = this.updateSettingsHostDownloadAction;
      }
      if (this.updateSettingsCategory != null) {
        torrent.category = this.updateSettingsCategory;
      }
      if (this.updateSettingsPriority != null) {
        torrent.priority = this.updateSettingsPriority;
      }
      if (this.updateSettingsDownloadRetryAttempts != null) {
        torrent.downloadRetryAttempts = this.updateSettingsDownloadRetryAttempts;
      }
      if (this.updateSettingsTorrentRetryAttempts != null) {
        torrent.torrentRetryAttempts = this.updateSettingsTorrentRetryAttempts;
      }
      if (this.updateSettingsDeleteOnError != null) {
        torrent.deleteOnError = this.updateSettingsDeleteOnError;
      }
      if (this.updateSettingsTorrentLifetime != null) {
        torrent.lifetime = this.updateSettingsTorrentLifetime;
      }

      calls.push(this.torrentService.update(torrent));
    });

    forkJoin(calls).subscribe({
      complete: () => {
        this.isChangeSettingsModalActive.set(false);
        this.changingSettings.set(false);

        this.selectedTorrents.set([]);
      },
      error: (err) => {
        this.changeSettingsError.set(err.error);
        this.changingSettings.set(false);
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

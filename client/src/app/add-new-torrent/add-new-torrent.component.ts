import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, ActivatedRoute } from '@angular/router';
import { TorrentService } from 'src/app/torrent.service';
import { Torrent, TorrentFileAvailability } from '../models/torrent.model';
import { SettingsService } from '../settings.service';
import { FormsModule } from '@angular/forms';
import { NgClass } from '@angular/common';

@Component({
  selector: 'app-add-new-torrent',
  templateUrl: './add-new-torrent.component.html',
  styleUrls: ['./add-new-torrent.component.scss'],
  imports: [FormsModule, NgClass],
  standalone: true,
})
export class AddNewTorrentComponent implements OnInit {
  private router = inject(Router);
  private torrentService = inject(TorrentService);
  private settingsService = inject(SettingsService);
  private activatedRoute = inject(ActivatedRoute);
  private destroyRef = inject(DestroyRef);

  public fileName: string;
  public readonly magnetLink = signal('');
  private currentTorrentFile: string;

  public provider: string = 'AllDebrid';
  public downloadClient: number = 0;

  public readonly category = signal('');
  public readonly hostDownloadAction = signal(0);
  public readonly downloadAction = signal(0);
  public readonly finishedAction = signal(0);
  public readonly finishedActionDelay = signal(0);
  public readonly downloadMinSize = signal(0);
  public readonly includeRegex = signal('');
  public readonly excludeRegex = signal('');
  public readonly torrentRetryAttempts = signal(1);
  public readonly downloadRetryAttempts = signal(3);
  public readonly torrentDeleteOnError = signal(0);
  public readonly torrentLifetime = signal(0);
  public readonly priority = signal<number | null>(null);

  public availableFiles: TorrentFileAvailability[];
  public downloadFiles: { [key: string]: boolean } = {};
  public allSelected: boolean;

  public readonly saving = signal(false);
  public readonly error = signal<string | null>(null);

  public readonly includeRegexError = signal<string | null>(null);
  public readonly excludeRegexError = signal<string | null>(null);
  public regexSelected: TorrentFileAvailability[];

  private selectedFile: File;

  ngOnInit(): void {
    this.activatedRoute.queryParams.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
      if (params['magnet']) {
        this.magnetLink.set(decodeURIComponent(params['magnet']));
      }
    });
    this.settingsService
      .get()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((settings) => {
        this.category.set(settings.find((m) => m.key === 'Downloads:Defaults:Category')?.value as string);
        this.hostDownloadAction.set(
          settings.find((m) => m.key === 'Downloads:Defaults:HostDownloadAction')?.value as number
        );
        this.downloadAction.set(
          settings.find((m) => m.key === 'Downloads:Defaults:OnlyDownloadAvailableFiles')?.value === true ? 1 : 0
        );
        this.finishedAction.set(settings.find((m) => m.key === 'Downloads:Defaults:FinishedAction')?.value as number);
        this.finishedActionDelay.set(
          settings.find((m) => m.key === 'Downloads:Defaults:FinishedActionDelay')?.value as number
        );
        this.downloadMinSize.set(settings.find((m) => m.key === 'Downloads:Defaults:MinFileSize')?.value as number);
        this.includeRegex.set(settings.find((m) => m.key === 'Downloads:Defaults:IncludeRegex')?.value as string);
        this.excludeRegex.set(settings.find((m) => m.key === 'Downloads:Defaults:ExcludeRegex')?.value as string);
        this.torrentRetryAttempts.set(
          settings.find((m) => m.key === 'Downloads:Defaults:TorrentRetryAttempts')?.value as number
        );
        this.downloadRetryAttempts.set(
          settings.find((m) => m.key === 'Downloads:Defaults:DownloadRetryAttempts')?.value as number
        );
        this.torrentDeleteOnError.set(
          settings.find((m) => m.key === 'Downloads:Defaults:DeleteOnError')?.value as number
        );
        this.torrentLifetime.set(settings.find((m) => m.key === 'Downloads:Defaults:TorrentLifetime')?.value as number);
        this.priority.set(settings.find((m) => m.key === 'Downloads:Defaults:Priority')?.value as number);
      });
  }

  public pickFile(evt: Event): void {
    const files = (evt.target as HTMLInputElement).files;

    if (files.length === 0) {
      return;
    }

    const file = files[0];

    this.fileName = file.name;

    this.selectedFile = file;

    this.checkFiles();
  }

  public ok(): void {
    this.saving.set(true);
    this.error.set(null);

    let downloadManualFiles: string = null;

    if (this.downloadAction() === 2) {
      const selectedFiles = [];
      for (const filePath in this.downloadFiles) {
        if (this.downloadFiles[filePath] === true) {
          selectedFiles.push(filePath);
        }
      }

      if (selectedFiles.length === 0) {
        this.error.set('No files have been selected to download');
        this.saving.set(false);
        return;
      }

      downloadManualFiles = selectedFiles.join(',');
    }

    const torrent = new Torrent();
    torrent.category = this.category();
    torrent.hostDownloadAction = this.hostDownloadAction();
    torrent.downloadAction = this.downloadAction();
    torrent.finishedAction = this.finishedAction();
    torrent.finishedActionDelay = this.finishedActionDelay();
    torrent.downloadMinSize = this.downloadMinSize();
    torrent.includeRegex = this.includeRegex();
    torrent.excludeRegex = this.excludeRegex();
    torrent.downloadManualFiles = downloadManualFiles;
    torrent.priority = this.priority();
    torrent.torrentRetryAttempts = this.torrentRetryAttempts();
    torrent.downloadRetryAttempts = this.downloadRetryAttempts();
    torrent.deleteOnError = this.torrentDeleteOnError();
    torrent.lifetime = this.torrentLifetime();
    torrent.downloadClient = this.downloadClient;

    if (this.magnetLink()) {
      this.torrentService.uploadMagnet(this.magnetLink(), torrent).subscribe({
        next: () => this.router.navigate(['/torrents']),
        error: (err) => {
          this.error.set(err.error);
          this.saving.set(false);
        },
      });
    } else if (this.selectedFile) {
      this.torrentService.uploadFile(this.selectedFile, torrent).subscribe({
        next: () => this.router.navigate(['/torrents']),
        error: (err) => {
          this.error.set(err.error);
          this.saving.set(false);
        },
      });
    } else {
      this.error.set('No magnet or file uploaded');
      this.saving.set(false);
    }
  }

  public onPaste(event: ClipboardEvent): void {
    const magnetLink = event.clipboardData?.getData('text');

    if (!magnetLink) {
      return;
    }

    event.preventDefault();
    this.magnetLink.set(magnetLink);
    this.checkFiles();
  }

  public checkFiles(): void {
    const magnetLink = this.magnetLink();

    if (magnetLink && magnetLink === this.currentTorrentFile) {
      return;
    }

    this.saving.set(true);
    this.error.set(null);
    this.availableFiles = null;
    this.downloadFiles = {};
    this.allSelected = true;

    if (magnetLink) {
      this.torrentService.checkFilesMagnet(magnetLink).subscribe({
        next: (result) => {
          this.saving.set(false);
          this.availableFiles = result;
          this.currentTorrentFile = magnetLink;
          result.forEach((file) => {
            this.downloadFiles[file.filename] = true;
          });
        },
        error: (err) => {
          this.error.set(err.error);
          this.saving.set(false);
        },
      });
    } else if (this.selectedFile) {
      this.torrentService.checkFiles(this.selectedFile).subscribe({
        next: (result) => {
          this.saving.set(false);
          this.availableFiles = result;
          result.forEach((file) => {
            this.downloadFiles[file.filename] = true;
          });
        },
        error: (err) => {
          this.error.set(err.error);
          this.saving.set(false);
        },
      });
    } else {
      this.saving.set(false);
    }
  }

  public verifyRegex(): void {
    this.includeRegexError.set(null);
    this.excludeRegexError.set(null);
    this.regexSelected = null;

    this.torrentService.verifyRegex(this.includeRegex(), this.excludeRegex(), this.magnetLink()).subscribe((result) => {
      this.includeRegexError.set(result.includeError);
      this.excludeRegexError.set(result.excludeError);
      this.regexSelected = result.selectedFiles;
    });
  }
}

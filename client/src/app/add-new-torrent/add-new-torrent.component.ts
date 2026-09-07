import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, ActivatedRoute } from '@angular/router';
import { TorrentService } from 'src/app/torrent.service';
import { Torrent } from '../models/torrent.model';
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

  public provider: string = 'AllDebrid';
  public downloadClient: number = 0;

  public readonly category = signal('');
  public readonly hostDownloadAction = signal(0);
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

  public readonly saving = signal(false);
  public readonly error = signal<string | null>(null);

  private selectedFile: File;

  ngOnInit(): void {
    this.activatedRoute.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
      const magnet = params.get('magnet');

      if (magnet) {
        // The router already decoded the handler parameter; preserve encoding inside the magnet itself.
        this.magnetLink.set(magnet);
      }
    });
    this.settingsService
      .get()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((settings) => {
        this.category.set(settings.find((m) => m.key === 'DownloadClient:Default:Category')?.value as string);
        this.hostDownloadAction.set(
          settings.find((m) => m.key === 'DownloadClient:Default:HostDownloadAction')?.value as number
        );
        this.finishedAction.set(
          settings.find((m) => m.key === 'DownloadClient:Default:FinishedAction')?.value as number
        );
        this.finishedActionDelay.set(
          settings.find((m) => m.key === 'DownloadClient:Default:FinishedActionDelay')?.value as number
        );
        this.downloadMinSize.set(settings.find((m) => m.key === 'DownloadClient:Default:MinFileSize')?.value as number);
        this.includeRegex.set(settings.find((m) => m.key === 'DownloadClient:Default:IncludeRegex')?.value as string);
        this.excludeRegex.set(settings.find((m) => m.key === 'DownloadClient:Default:ExcludeRegex')?.value as string);
        this.torrentRetryAttempts.set(
          settings.find((m) => m.key === 'DownloadClient:Default:TorrentRetryAttempts')?.value as number
        );
        this.downloadRetryAttempts.set(
          settings.find((m) => m.key === 'DownloadClient:Default:DownloadRetryAttempts')?.value as number
        );
        this.torrentDeleteOnError.set(
          settings.find((m) => m.key === 'DownloadClient:Default:DeleteOnError')?.value as number
        );
        this.torrentLifetime.set(
          settings.find((m) => m.key === 'DownloadClient:Default:TorrentLifetime')?.value as number
        );
        this.priority.set(settings.find((m) => m.key === 'DownloadClient:Default:Priority')?.value as number);
      });
  }

  public pickFile(evt: Event): void {
    const files = (evt.target as HTMLInputElement).files;
    const file = files?.item(0);

    if (!file) {
      return;
    }

    this.fileName = file.name;
    this.selectedFile = file;
  }

  public ok(): void {
    this.error.set(null);
    this.saving.set(true);

    const torrent = new Torrent();
    torrent.category = this.category();
    torrent.hostDownloadAction = this.hostDownloadAction();
    torrent.finishedAction = this.finishedAction();
    torrent.finishedActionDelay = this.finishedActionDelay();
    torrent.downloadMinSize = this.downloadMinSize();
    torrent.includeRegex = this.includeRegex();
    torrent.excludeRegex = this.excludeRegex();
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
  }
}

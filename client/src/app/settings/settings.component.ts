import { KeyValuePipe, NgClass } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { finalize, switchMap, tap } from 'rxjs';
import { SettingsService } from 'src/app/settings.service';
import { AuthService } from '../auth.service';
import { FileSizePipe } from '../filesize.pipe';
import { Setting } from '../models/setting.model';
import { Nl2BrPipe } from '../nl2br.pipe';
import { MagnetHandlerComponent } from './magnet-handler/magnet-handler.component';

@Component({
  selector: 'app-settings',
  host: { class: 'page-layout' },
  templateUrl: './settings.component.html',
  styleUrls: ['./settings.component.scss'],
  imports: [NgClass, FormsModule, KeyValuePipe, Nl2BrPipe, FileSizePipe, MagnetHandlerComponent],
  standalone: true,
})
export class SettingsComponent implements OnInit {
  private settingsService = inject(SettingsService);
  private authService = inject(AuthService);

  public readonly diagnosticsView = 'diagnostics';
  public readonly accountView = 'account';

  public readonly activeView = signal('');
  public readonly loading = signal(true);
  public readonly loadError = signal<string | null>(null);

  public readonly profileUsername = signal('');
  public readonly profilePassword = signal('');
  public readonly profileSaving = signal(false);
  public readonly profileSuccess = signal(false);
  public readonly profileError = signal<string | null>(null);

  public readonly tabs = signal<Setting[]>([]);
  private settingMap = new Map<string, Setting>();
  private visibleSecrets = new Set<string>();

  public readonly settingsSaving = signal(false);
  public readonly settingsSaveSuccess = signal(false);
  public readonly settingsSaveError = signal<string | null>(null);

  public readonly pathTesting = signal(false);
  public readonly testPathError = signal<string | null>(null);
  public readonly testPathSuccess = signal(false);

  public readonly downloadSpeedTesting = signal(false);
  public readonly testDownloadSpeedError = signal<string | null>(null);
  public readonly testDownloadSpeedSuccess = signal<number | null>(null);

  public readonly writeSpeedTesting = signal(false);
  public readonly testWriteSpeedError = signal<string | null>(null);
  public readonly testWriteSpeedSuccess = signal<number | null>(null);

  ngOnInit(): void {
    this.loadSettings();
  }

  public loadSettings(): void {
    this.loading.set(true);
    this.loadError.set(null);

    this.settingsService
      .get()
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (settings) => this.applySettings(settings),
        error: (error) => {
          this.loadError.set(this.getErrorMessage(error, 'Settings could not be loaded.'));
        },
      });
  }

  private applySettings(settings: Setting[]): void {
    const tabs = settings.filter((setting) => setting.type === 'Object' && !setting.parentKey);

    for (const tab of tabs) {
      const prefix = `${tab.key}:`;
      tab.settings = settings.filter(
        (setting) => setting.parentKey === tab.key || setting.parentKey?.startsWith(prefix)
      );
    }

    this.settingMap = new Map(settings.map((setting) => [setting.key, setting]));
    this.visibleSecrets.clear();
    this.tabs.set(tabs);

    if (!this.activeView() || !this.isKnownView(this.activeView())) {
      this.activeView.set(tabs[0]?.key ?? this.diagnosticsView);
    }
  }

  private isKnownView(view: string): boolean {
    return view === this.diagnosticsView || view === this.accountView || this.tabs().some((tab) => tab.key === view);
  }

  public selectView(view: string): void {
    this.activeView.set(view);
    this.settingsSaveError.set(null);
    this.settingsSaveSuccess.set(false);
  }

  public saveSettings(): void {
    if (this.settingsSaving()) {
      return;
    }

    this.settingsSaving.set(true);
    this.settingsSaveSuccess.set(false);
    this.settingsSaveError.set(null);

    const settingsToSave = this.tabs()
      .flatMap((tab) => tab.settings)
      .filter((setting) => setting.type !== 'Object');
    let updateCompleted = false;

    this.settingsService
      .update(settingsToSave)
      .pipe(
        tap(() => (updateCompleted = true)),
        switchMap(() => this.settingsService.get()),
        finalize(() => this.settingsSaving.set(false))
      )
      .subscribe({
        next: (settings) => {
          this.applySettings(settings);
          this.settingsSaveSuccess.set(true);
        },
        error: (error) => {
          const fallback = updateCompleted
            ? 'Settings were saved, but the current values could not be reloaded.'
            : 'Settings could not be saved.';
          this.settingsSaveError.set(this.getErrorMessage(error, fallback));
        },
      });
  }

  private getSetting(key: string): string {
    return (this.settingMap.get(key)?.value as string) || '';
  }

  public testDownloadPath(): void {
    const downloadPath = this.getSetting('Paths:DownloadPath');

    this.pathTesting.set(true);
    this.testPathError.set(null);
    this.testPathSuccess.set(false);

    this.settingsService
      .testPath(downloadPath)
      .pipe(finalize(() => this.pathTesting.set(false)))
      .subscribe({
        next: () => {
          this.testPathSuccess.set(true);
        },
        error: (error) => {
          this.testPathError.set(this.getErrorMessage(error, 'The download path could not be tested.'));
        },
      });
  }

  public testDownloadSpeed(): void {
    this.downloadSpeedTesting.set(true);
    this.testDownloadSpeedError.set(null);
    this.testDownloadSpeedSuccess.set(null);

    this.settingsService
      .testDownloadSpeed()
      .pipe(finalize(() => this.downloadSpeedTesting.set(false)))
      .subscribe({
        next: (result) => {
          this.testDownloadSpeedSuccess.set(result);
        },
        error: (error) => {
          this.testDownloadSpeedError.set(this.getErrorMessage(error, 'The download speed test failed.'));
        },
      });
  }

  public testWriteSpeed(): void {
    this.writeSpeedTesting.set(true);
    this.testWriteSpeedError.set(null);
    this.testWriteSpeedSuccess.set(null);

    this.settingsService
      .testWriteSpeed()
      .pipe(finalize(() => this.writeSpeedTesting.set(false)))
      .subscribe({
        next: (result) => {
          this.testWriteSpeedSuccess.set(result);
        },
        error: (error) => {
          this.testWriteSpeedError.set(this.getErrorMessage(error, 'The write speed test failed.'));
        },
      });
  }

  public getPlaceholder(setting: Setting): string {
    switch (setting.key) {
      case 'Paths:MappedPath':
        return this.getSetting('Paths:DownloadPath') || 'Same as the local download path';
      case 'Paths:WatchErrorPath':
      case 'Paths:WatchProcessedPath': {
        const inboxPath = this.getSetting('Paths:WatchPath');
        const subfolder = setting.key === 'Paths:WatchErrorPath' ? 'error' : 'processed';

        if (!inboxPath) {
          return `Inside the inbox (${subfolder})`;
        }

        const separator = inboxPath.includes('\\') && !inboxPath.includes('/') ? '\\' : '/';
        return `${inboxPath.replace(/[\\/]+$/, '')}${separator}${subfolder}`;
      }
      default:
        return '';
    }
  }

  public isSecretVisible(setting: Setting): boolean {
    return this.visibleSecrets.has(setting.key);
  }

  public toggleSecretVisibility(setting: Setting): void {
    if (this.visibleSecrets.has(setting.key)) {
      this.visibleSecrets.delete(setting.key);
      return;
    }

    this.visibleSecrets.add(setting.key);
  }

  public saveProfile(): void {
    if (this.profileSaving() || (!this.profileUsername() && !this.profilePassword())) {
      return;
    }

    this.profileSuccess.set(false);
    this.profileError.set(null);
    this.profileSaving.set(true);

    this.authService
      .update(this.profileUsername(), this.profilePassword())
      .pipe(finalize(() => this.profileSaving.set(false)))
      .subscribe({
        next: () => {
          this.profileUsername.set('');
          this.profilePassword.set('');
          this.profileSuccess.set(true);
        },
        error: (error) => {
          this.profileError.set(this.getErrorMessage(error, 'Account credentials could not be updated.'));
          this.profileSuccess.set(false);
        },
      });
  }

  private getErrorMessage(error: unknown, fallback: string): string {
    const payload = error instanceof HttpErrorResponse ? error.error : error;

    if (typeof payload === 'string' && payload.trim()) {
      return payload.trim();
    }

    if (payload && typeof payload === 'object') {
      const response = payload as Record<string, unknown>;

      for (const key of ['detail', 'title', 'message']) {
        const value = response[key];

        if (typeof value === 'string' && value.trim()) {
          return value.trim();
        }
      }

      const validationErrors = response['errors'];

      if (validationErrors && typeof validationErrors === 'object') {
        const messages = Object.values(validationErrors as Record<string, unknown>).flatMap((value) =>
          Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : []
        );

        if (messages.length > 0) {
          return messages.join(' ');
        }
      }
    }

    if (error instanceof Error && error.message) {
      return error.message;
    }

    return fallback;
  }
}

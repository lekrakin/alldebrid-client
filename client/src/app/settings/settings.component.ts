import { NgClass, KeyValuePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { finalize, switchMap, tap } from 'rxjs';
import { SettingsService } from 'src/app/settings.service';
import { AuthService } from '../auth.service';
import { FileSizePipe } from '../filesize.pipe';
import { Setting } from '../models/setting.model';
import { Nl2BrPipe } from '../nl2br.pipe';

@Component({
  selector: 'app-settings',
  templateUrl: './settings.component.html',
  styleUrls: ['./settings.component.scss'],
  imports: [NgClass, FormsModule, KeyValuePipe, Nl2BrPipe, FileSizePipe],
  standalone: true,
})
export class SettingsComponent implements OnInit {
  private settingsService = inject(SettingsService);
  private authService = inject(AuthService);

  public readonly diagnosticsView = 'diagnostics';
  public readonly accountView = 'account';

  public activeView = '';
  public loading = true;
  public loadError: string = null;

  public profileUsername: string;
  public profilePassword: string;
  public profileSaving = false;
  public profileSuccess = false;
  public profileError: string = null;

  public tabs: Setting[] = [];
  private settingMap = new Map<string, Setting>();
  private visibleSecrets = new Set<string>();

  public settingsSaving = false;
  public settingsSaveSuccess = false;
  public settingsSaveError: string = null;

  public pathTesting = false;
  public testPathError: string = null;
  public testPathSuccess = false;

  public downloadSpeedTesting = false;
  public testDownloadSpeedError: string = null;
  public testDownloadSpeedSuccess: number = null;

  public writeSpeedTesting = false;
  public testWriteSpeedError: string = null;
  public testWriteSpeedSuccess: number = null;

  public canRegisterMagnetHandler = false;
  public magnetHandlerSuccess = false;
  public magnetHandlerError: string = null;

  ngOnInit(): void {
    this.loadSettings();
    this.canRegisterMagnetHandler = !!(window.isSecureContext && 'registerProtocolHandler' in navigator);
  }

  public loadSettings(): void {
    this.loading = true;
    this.loadError = null;

    this.settingsService
      .get()
      .pipe(finalize(() => (this.loading = false)))
      .subscribe({
        next: (settings) => this.applySettings(settings),
        error: (error) => {
          this.loadError = this.getErrorMessage(error, 'Settings could not be loaded.');
        },
      });
  }

  private applySettings(settings: Setting[]): void {
    this.tabs = settings.filter((setting) => !setting.key.includes(':'));

    for (const tab of this.tabs) {
      const prefix = `${tab.key}:`;
      tab.settings = settings.filter((setting) => setting.key.startsWith(prefix));
    }

    this.settingMap = new Map(settings.map((setting) => [setting.key, setting]));
    this.visibleSecrets.clear();

    if (!this.activeView || !this.isKnownView(this.activeView)) {
      this.activeView = this.tabs[0]?.key ?? this.diagnosticsView;
    }
  }

  private isKnownView(view: string): boolean {
    return view === this.diagnosticsView || view === this.accountView || this.tabs.some((tab) => tab.key === view);
  }

  public selectView(view: string): void {
    this.activeView = view;
    this.settingsSaveError = null;
    this.settingsSaveSuccess = false;
  }

  public saveSettings(): void {
    if (this.settingsSaving) {
      return;
    }

    this.settingsSaving = true;
    this.settingsSaveSuccess = false;
    this.settingsSaveError = null;

    const settingsToSave = this.tabs.flatMap((tab) => tab.settings).filter((setting) => setting.type !== 'Object');
    let updateCompleted = false;

    this.settingsService
      .update(settingsToSave)
      .pipe(
        tap(() => (updateCompleted = true)),
        switchMap(() => this.settingsService.get()),
        finalize(() => (this.settingsSaving = false))
      )
      .subscribe({
        next: (settings) => {
          this.applySettings(settings);
          this.settingsSaveSuccess = true;
        },
        error: (error) => {
          const fallback = updateCompleted
            ? 'Settings were saved, but the current values could not be reloaded.'
            : 'Settings could not be saved.';
          this.settingsSaveError = this.getErrorMessage(error, fallback);
        },
      });
  }

  private getSetting(key: string): string {
    return (this.settingMap.get(key)?.value as string) || '';
  }

  public testDownloadPath(): void {
    const downloadPath = this.getSetting('Storage:DownloadPath');

    this.pathTesting = true;
    this.testPathError = null;
    this.testPathSuccess = false;

    this.settingsService
      .testPath(downloadPath)
      .pipe(finalize(() => (this.pathTesting = false)))
      .subscribe({
        next: () => {
          this.testPathSuccess = true;
        },
        error: (error) => {
          this.testPathError = this.getErrorMessage(error, 'The download path could not be tested.');
        },
      });
  }

  public testDownloadSpeed(): void {
    this.downloadSpeedTesting = true;
    this.testDownloadSpeedError = null;
    this.testDownloadSpeedSuccess = null;

    this.settingsService
      .testDownloadSpeed()
      .pipe(finalize(() => (this.downloadSpeedTesting = false)))
      .subscribe({
        next: (result) => {
          this.testDownloadSpeedSuccess = result;
        },
        error: (error) => {
          this.testDownloadSpeedError = this.getErrorMessage(error, 'The download speed test failed.');
        },
      });
  }

  public testWriteSpeed(): void {
    this.writeSpeedTesting = true;
    this.testWriteSpeedError = null;
    this.testWriteSpeedSuccess = null;

    this.settingsService
      .testWriteSpeed()
      .pipe(finalize(() => (this.writeSpeedTesting = false)))
      .subscribe({
        next: (result) => {
          this.testWriteSpeedSuccess = result;
        },
        error: (error) => {
          this.testWriteSpeedError = this.getErrorMessage(error, 'The write speed test failed.');
        },
      });
  }

  public getPlaceholder(setting: Setting): string {
    switch (setting.key) {
      case 'Integrations:ReportedDownloadPath':
        return this.getSetting('Storage:DownloadPath') || 'Same as the local download path';
      case 'WatchFolder:ErrorPath':
      case 'WatchFolder:ProcessedPath': {
        const inboxPath = this.getSetting('WatchFolder:InboxPath');
        const subfolder = setting.key === 'WatchFolder:ErrorPath' ? 'error' : 'processed';

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
    if (this.profileSaving || (!this.profileUsername && !this.profilePassword)) {
      return;
    }

    this.profileSuccess = false;
    this.profileError = null;
    this.profileSaving = true;

    this.authService
      .update(this.profileUsername, this.profilePassword)
      .pipe(finalize(() => (this.profileSaving = false)))
      .subscribe({
        next: () => {
          this.profileUsername = '';
          this.profilePassword = '';
          this.profileSuccess = true;
        },
        error: (error) => {
          this.profileError = this.getErrorMessage(error, 'Account credentials could not be updated.');
          this.profileSuccess = false;
        },
      });
  }

  public registerMagnetHandler(): void {
    this.magnetHandlerSuccess = false;
    this.magnetHandlerError = null;

    try {
      navigator.registerProtocolHandler('magnet', `${window.location.origin}/add?magnet=%s`);
      this.magnetHandlerSuccess = true;
    } catch (error) {
      this.magnetHandlerError = this.getErrorMessage(error, 'Magnet link registration failed.');
    }
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

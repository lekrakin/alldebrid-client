import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Observable, ReplaySubject } from 'rxjs';
import { Torrent, TorrentFileAvailability } from './models/torrent.model';
import { APP_BASE_HREF } from '@angular/common';

@Injectable({
  providedIn: 'root',
})
export class TorrentService {
  private static readonly reconnectDelaysMilliseconds = [2_000, 5_000, 10_000, 30_000] as const;

  private http = inject(HttpClient);
  private baseHref = inject(APP_BASE_HREF);

  public readonly update$ = new ReplaySubject<Torrent[]>(1);

  private connection: signalR.HubConnection | null = null;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private reconnectAttempt = 0;
  private connectionStartPending = false;

  constructor() {
    this.connect();
  }

  private connect(): void {
    if (this.connection != null) {
      return;
    }

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(`${this.baseHref}hub`)
      .withAutomaticReconnect()
      .build();

    this.connection.on('update', (torrents: Torrent[]) => {
      this.update$.next(torrents);
    });

    this.connection.onclose(() => this.scheduleReconnect());
    void this.startConnection();
  }

  private async startConnection(): Promise<void> {
    if (
      this.connection === null ||
      this.connectionStartPending ||
      this.connection.state !== signalR.HubConnectionState.Disconnected
    ) {
      return;
    }

    this.connectionStartPending = true;

    try {
      await this.connection.start();
      this.reconnectAttempt = 0;
    } catch (error) {
      console.error('Could not connect to the live torrent update stream. Retrying.', error);
      this.scheduleReconnect();
    } finally {
      this.connectionStartPending = false;
    }
  }

  private scheduleReconnect(): void {
    if (this.reconnectTimer !== null) {
      return;
    }

    const delayIndex = Math.min(this.reconnectAttempt, TorrentService.reconnectDelaysMilliseconds.length - 1);
    const delay = TorrentService.reconnectDelaysMilliseconds[delayIndex];
    this.reconnectAttempt += 1;

    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      void this.startConnection();
    }, delay);
  }

  public getList(): Observable<Torrent[]> {
    return this.http.get<Torrent[]>(`${this.baseHref}Api/Torrents`);
  }

  public get(torrentId: string): Observable<Torrent> {
    return this.http.get<Torrent>(`${this.baseHref}Api/Torrents/Get/${torrentId}`);
  }

  public uploadMagnet(magnetLink: string, torrent: Torrent): Observable<void> {
    return this.http.post<void>(`${this.baseHref}Api/Torrents/UploadMagnet`, {
      magnetLink,
      torrent,
    });
  }

  public uploadFile(file: File, torrent: Torrent): Observable<void> {
    const formData: FormData = new FormData();
    formData.append('file', file);
    formData.append('formData', JSON.stringify({ torrent }));
    return this.http.post<void>(`${this.baseHref}Api/Torrents/UploadFile`, formData);
  }

  public checkFilesMagnet(magnetLink: string): Observable<TorrentFileAvailability[]> {
    return this.http.post<TorrentFileAvailability[]>(`${this.baseHref}Api/Torrents/CheckFilesMagnet`, {
      magnetLink,
    });
  }

  public checkFiles(file: File): Observable<TorrentFileAvailability[]> {
    const formData: FormData = new FormData();
    formData.append('file', file);
    return this.http.post<TorrentFileAvailability[]>(`${this.baseHref}Api/Torrents/CheckFiles`, formData);
  }

  public delete(
    torrentId: string,
    deleteData: boolean,
    deleteRdTorrent: boolean,
    deleteLocalFiles: boolean
  ): Observable<void> {
    return this.http.post<void>(`${this.baseHref}Api/Torrents/Delete/${torrentId}`, {
      deleteData,
      deleteRdTorrent,
      deleteLocalFiles,
    });
  }

  public retry(torrentId: string): Observable<void> {
    return this.http.post<void>(`${this.baseHref}Api/Torrents/Retry/${torrentId}`, {});
  }

  public retryDownload(downloadId: string): Observable<void> {
    return this.http.post<void>(`${this.baseHref}Api/Torrents/RetryDownload/${downloadId}`, {});
  }

  public update(torrent: Torrent): Observable<void> {
    return this.http.put<void>(`${this.baseHref}Api/Torrents/Update`, torrent);
  }

  public verifyRegex(
    includeRegex: string,
    excludeRegex: string,
    magnetLink: string
  ): Observable<{ includeError: string; excludeError: string; selectedFiles: TorrentFileAvailability[] }> {
    return this.http.post<{ includeError: string; excludeError: string; selectedFiles: TorrentFileAvailability[] }>(
      `${this.baseHref}Api/Torrents/VerifyRegex`,
      {
        includeRegex,
        excludeRegex,
        magnetLink,
      }
    );
  }
}

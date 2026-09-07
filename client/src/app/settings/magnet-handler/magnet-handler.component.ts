import { APP_BASE_HREF, DOCUMENT } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import {
  getLocalSettingsUrl,
  getMagnetHandlerAvailability,
  MagnetHandlerRequest,
  requestMagnetHandler,
} from './magnet-handler';

@Component({
  selector: 'app-magnet-handler',
  templateUrl: './magnet-handler.component.html',
  styleUrl: './magnet-handler.component.scss',
  standalone: true,
})
export class MagnetHandlerComponent {
  private readonly browser = inject(DOCUMENT).defaultView;
  private readonly baseHref = inject(APP_BASE_HREF);

  public readonly availability = getMagnetHandlerAvailability(this.browser);
  public readonly localSettingsUrl = getLocalSettingsUrl(this.browser, this.baseHref);
  public readonly request = signal<MagnetHandlerRequest | null>(null);

  public register(): void {
    this.request.set(requestMagnetHandler(this.browser, this.baseHref));
  }
}

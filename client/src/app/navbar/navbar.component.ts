import { Component, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { AuthService } from '../auth.service';
import { Profile } from '../models/profile.model';
import { SettingsService } from '../settings.service';

@Component({
  selector: 'app-navbar',
  templateUrl: './navbar.component.html',
  styleUrls: ['./navbar.component.scss'],
  imports: [RouterLink],
  standalone: true,
})
export class NavbarComponent implements OnInit {
  private settingsService = inject(SettingsService);
  private authService = inject(AuthService);
  private router = inject(Router);

  public readonly showMobileMenu = signal(false);

  public readonly profile = signal<Profile | null>(null);
  public readonly providerLink = 'https://alldebrid.com/account/';
  public readonly version = signal('');
  public premiumDays(): number {
    const expiration = this.profile()?.expiration;

    if (!expiration) {
      return 0;
    }

    const diff = new Date(expiration).getTime() - Date.now();

    return Math.max(0, Math.ceil(diff / (1000 * 60 * 60 * 24)));
  }

  constructor() {
    this.router.events.pipe(takeUntilDestroyed()).subscribe((event) => {
      if (event instanceof NavigationEnd) {
        this.showMobileMenu.set(false);
      }
    });
  }

  ngOnInit(): void {
    this.settingsService.getProfile().subscribe((result) => {
      this.profile.set(result);
    });

    this.settingsService.getVersion().subscribe((result) => {
      this.version.set(result.version);
    });
  }

  public toggleMobileMenu(): void {
    this.showMobileMenu.update((isOpen) => !isOpen);
  }

  public logout(): void {
    this.authService.logout().subscribe({ next: () => this.router.navigate(['/login']), error: console.error });
  }
}

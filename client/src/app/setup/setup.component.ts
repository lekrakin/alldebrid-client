import { Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../auth.service';
import { NgClass } from '@angular/common';
import { FormsModule } from '@angular/forms';

@Component({
  selector: 'app-setup',
  templateUrl: './setup.component.html',
  imports: [FormsModule, NgClass],
  standalone: true,
})
export class SetupComponent {
  private authService = inject(AuthService);
  private router = inject(Router);

  public userName: string;
  public password: string;
  public token: string;

  public readonly error = signal<string | null>(null);
  public readonly working = signal(false);

  public readonly step = signal(1);

  public setup(): void {
    this.error.set(null);
    this.working.set(true);

    this.authService.create(this.userName, this.password).subscribe({
      next: (response) => {
        this.step.set(response.providerConfigured ? 3 : 2);
        this.working.set(false);
      },
      error: (err) => {
        this.working.set(false);
        this.error.set(err.error);
      },
    });
  }

  public setToken(): void {
    this.working.set(true);
    this.error.set(null);

    this.authService.setupProvider(this.token).subscribe({
      next: () => {
        this.step.set(3);
        this.working.set(false);
      },
      error: (err: any) => {
        this.working.set(false);
        this.error.set(err.error);
      },
    });
  }

  public close(): void {
    this.router.navigate(['/torrents']);
  }
}

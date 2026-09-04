import { Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../auth.service';
import { FormsModule } from '@angular/forms';
import { NgClass } from '@angular/common';

@Component({
  selector: 'app-login',
  templateUrl: './login.component.html',
  imports: [FormsModule, NgClass],
  standalone: true,
})
export class LoginComponent {
  private authService = inject(AuthService);
  private router = inject(Router);

  public userName: string;
  public password: string;
  public readonly error = signal<string | null>(null);
  public readonly loggingIn = signal(false);

  public setUserName(event: Event): void {
    this.userName = (event.target as any).value;
  }

  public setPassword(event: Event): void {
    this.password = (event.target as any).value;
  }

  public login(): void {
    this.error.set(null);
    this.loggingIn.set(true);
    this.authService.login(this.userName, this.password).subscribe({
      next: () => this.router.navigate(['/torrents']),
      error: (err) => {
        this.loggingIn.set(false);
        this.error.set(err.error);
      },
    });
  }
}

import { APP_BASE_HREF } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface AuthCreateResponse {
  providerConfigured: boolean;
}

@Injectable({
  providedIn: 'root',
})
export class AuthService {
  private http = inject(HttpClient);
  private baseHref = inject(APP_BASE_HREF);

  public isLoggedIn(): Observable<boolean> {
    return this.http.get<boolean>(`${this.baseHref}Api/Authentication/IsLoggedIn`);
  }

  public create(userName: string, password: string): Observable<AuthCreateResponse> {
    return this.http.post<AuthCreateResponse>(`${this.baseHref}Api/Authentication/Create`, {
      userName,
      password,
    });
  }

  public setupProvider(token: string): Observable<void> {
    return this.http.post<void>(`${this.baseHref}Api/Authentication/SetupProvider`, {
      provider: 0,
      token,
    });
  }

  public login(userName: string, password: string): Observable<void> {
    return this.http.post<void>(`${this.baseHref}Api/Authentication/Login`, {
      userName,
      password,
    });
  }

  public logout() {
    return this.http.post<void>(`${this.baseHref}Api/Authentication/Logout`, {});
  }

  public update(userName: string, password: string): Observable<void> {
    return this.http.post<void>(`${this.baseHref}Api/Authentication/Update`, {
      userName,
      password,
    });
  }
}

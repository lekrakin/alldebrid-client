import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { AddNewTorrentComponent } from './add-new-torrent/add-new-torrent.component';
import { authResolver } from './auth-resolver.service';
import { LoginComponent } from './login/login.component';
import { MainLayoutComponent } from './main-layout/main-layout.component';
import { SettingsComponent } from './settings/settings.component';
import { SetupComponent } from './setup/setup.component';
import { TorrentTableComponent } from './torrent-table/torrent-table.component';
import { TorrentComponent } from './torrent/torrent.component';

const routes: Routes = [
  {
    path: 'login',
    component: LoginComponent,
  },
  {
    path: 'setup',
    component: SetupComponent,
  },
  {
    path: '',
    component: MainLayoutComponent,
    resolve: {
      isLoggedIn: authResolver,
    },
    children: [
      {
        path: '',
        redirectTo: 'torrents',
        pathMatch: 'full',
      },
      {
        path: 'torrents',
        component: TorrentTableComponent,
      },
      {
        path: 'index.html',
        redirectTo: 'torrents',
        pathMatch: 'full',
      },
      {
        path: 'torrent/:id',
        component: TorrentComponent,
      },
      {
        path: 'add',
        component: AddNewTorrentComponent,
      },
      {
        path: 'settings',
        component: SettingsComponent,
      },
      {
        path: 'profile',
        redirectTo: 'settings',
        pathMatch: 'full',
      },
      {
        path: '**',
        redirectTo: 'torrents',
      },
    ],
  },
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule],
})
export class AppRoutingModule {}

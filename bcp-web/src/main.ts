import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { AppComponent } from './app/app.component';
import { clearEphemeralAppStorageOnce } from './app/services/clear-app-storage';

clearEphemeralAppStorageOnce();

bootstrapApplication(AppComponent, appConfig)
  .catch((err) => console.error(err));

import { ApplicationConfig, APP_INITIALIZER, provideZoneChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';

import { routes } from './app.routes';
import { legacyApiAuthInterceptor } from './services/legacy-api-auth.interceptor';
import { ThemeService } from './services/theme.service';
import { AppConfigService } from './services/app-config.service';

function initTheme(theme: ThemeService) {
  return () => {
    theme.resolvedTheme();
  };
}

function initAppConfig(config: AppConfigService) {
  return () => config.load();
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes),
    provideHttpClient(withInterceptors([legacyApiAuthInterceptor])),
    {
      provide: APP_INITIALIZER,
      useFactory: initAppConfig,
      deps: [AppConfigService],
      multi: true,
    },
    {
      provide: APP_INITIALIZER,
      useFactory: initTheme,
      deps: [ThemeService],
      multi: true,
    },
  ],
};

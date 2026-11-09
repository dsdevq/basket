import {Location} from '@angular/common';
import {ChangeDetectionStrategy, Component, inject} from '@angular/core';
import {toSignal} from '@angular/core/rxjs-interop';
import {
  NavigationCancel,
  NavigationCancellationCode,
  NavigationEnd,
  NavigationError,
  Router,
  RouterOutlet,
} from '@angular/router';
import {filter, map} from 'rxjs';

import {AppShellComponent} from './core/shell/app-shell.component';
import {type PendingPage, pendingPage} from './core/shell/pending-route';

/**
 * Draws the signed-in shell at once. The first navigation waits for the session call (the guards) and
 * the page's chunk; until it ends, the shell is drawn with its account pending and a skeleton of the
 * page the address names, so nothing moves when the real page arrives (Apple HIG, Loading and
 * Launching; Carbon, Loading pattern).
 */
@Component({
  selector: 'bk-root',
  imports: [RouterOutlet, AppShellComponent],
  template: `
    @if (pending(); as page) {
      <bk-app-shell [pending]="page" />
    } @else {
      <router-outlet />
    }
  `,
  host: {class: 'block h-full'},
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppComponent {
  private readonly firstNavigationDone = toSignal(
    inject(Router).events.pipe(
      filter(
        event =>
          event instanceof NavigationEnd ||
          (event instanceof NavigationCancel &&
            event.code !== NavigationCancellationCode.Redirect &&
            event.code !== NavigationCancellationCode.SupersededByNewNavigation) ||
          event instanceof NavigationError
      ),
      map(() => true)
    ),
    {initialValue: false}
  );
  private readonly startPage = pendingPage(inject(Location).path());

  protected readonly pending = (): PendingPage | null =>
    this.firstNavigationDone() ? null : this.startPage;
}

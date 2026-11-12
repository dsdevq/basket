import {
  ChangeDetectionStrategy,
  Component,
  CUSTOM_ELEMENTS_SCHEMA,
  inject,
  input,
} from '@angular/core';
import {RouterOutlet} from '@angular/router';
import {AppUpdateService} from '@lifekit-hq/core/pwa';
import {AppLayoutComponent, type MenuItem, PageComponent, SkeletonComponent} from '@lifekit-hq/ui';

import type {PendingPage} from './pending-route';
import {ShellChromeService} from './shell-chrome.service';

/** The signed-in frame: the lifekit app layout with List, Shelf, Plan and Cards as the tabs and Prices, Promotions, People and Settings behind More. */
@Component({
  selector: 'bk-app-shell',
  imports: [AppLayoutComponent, PageComponent, RouterOutlet, SkeletonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  schemas: [CUSTOM_ELEMENTS_SCHEMA],
  template: `
    <cmn-app-layout
      [navItems]="chrome.navItems()"
      [paletteItems]="chrome.paletteItems()"
      [tabRoutes]="chrome.tabRoutes"
      [account]="chrome.account()"
      [accountPending]="!!pending()"
      [versionLabel]="chrome.versionLabel"
      [versionDot]="chrome.versionDot()"
      [phoneOverlay]="true"
      (avatarMenuSelect)="onMenu($event)"
      (paletteAction)="onPaletteAction($event)"
      (versionClick)="chrome.openWhatsNew()"
      brand="basket"
      moreRoute="/more"
    >
      <div cmnAppLayoutNotice>
        <lk-offline-banner />
        <lk-update-prompt
          [ready]="update.updateReady()"
          (lk-update-prompt-reload)="update.reload()"
        />
        <lk-install-hint />
      </div>
      @if (pending(); as page) {
        <cmn-page [kind]="page.kind" [title]="page.title" fill>
          <div role="status" aria-label="Loading">
            <cmn-skeleton [shape]="page.shape" />
          </div>
        </cmn-page>
      } @else {
        <router-outlet />
      }
    </cmn-app-layout>
  `,
})
export class AppShellComponent {
  /** The page to hold while the session call is out; unset once the routed page can show. */
  public readonly pending = input<PendingPage | null>(null);

  protected readonly chrome = inject(ShellChromeService);
  protected readonly update = inject(AppUpdateService);

  /** The layout navigates to Settings itself; the app handles What's new and Sign out. */
  protected async onMenu(item: MenuItem): Promise<void> {
    await this.chrome.handle(item.id);
  }

  /** The layout navigates to pages and applies the appearance entries itself. */
  protected async onPaletteAction(id: string): Promise<void> {
    await this.chrome.handle(id);
  }
}

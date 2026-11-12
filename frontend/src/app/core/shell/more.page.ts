import {ChangeDetectionStrategy, Component, inject} from '@angular/core';
import {type MenuItem, MorePageComponent as MoreKit} from '@lifekit-hq/ui';

import {ShellChromeService} from './shell-chrome.service';

/** Phone "More" tab: the pages past the tab bar, then the account. The kit is its own `cmn-page`. */
@Component({
  selector: 'bk-more-page',
  imports: [MoreKit],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-more-page
      [items]="chrome.navItems()"
      [tabRoutes]="chrome.tabRoutes"
      [account]="chrome.account()"
      [versionLabel]="chrome.versionLabel"
      (itemSelect)="select($event)"
    />
  `,
})
export class MorePage {
  protected readonly chrome = inject(ShellChromeService);

  protected async select(item: MenuItem): Promise<void> {
    await this.chrome.handle(item.id);
  }
}

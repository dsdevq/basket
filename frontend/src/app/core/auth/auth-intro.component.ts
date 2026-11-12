import {ChangeDetectionStrategy, Component, input} from '@angular/core';
import {AppMarkComponent} from '@lifekit-hq/ui';

/** The host of every page shown before sign-in: the page's main landmark, on the app background. */
export const AUTH_PAGE_HOST = {role: 'main', class: 'block min-h-dvh bg-surface-bg'} as const;

/** The top of a page shown before sign-in, inside its `cmn-page`: the app mark, then the lead. */
@Component({
  selector: 'bk-auth-intro',
  imports: [AppMarkComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {class: 'contents'},
  template: `
    <cmn-app-mark [size]="56" />
    @if (lead()) {
      <p class="text-cmn-sm text-text-secondary">{{ lead() }}</p>
    }
  `,
})
export class AuthIntroComponent {
  public readonly lead = input<string>('');
}

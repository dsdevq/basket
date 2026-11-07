import {NgOptimizedImage} from '@angular/common';
import {ChangeDetectionStrategy, Component, input} from '@angular/core';

/** The host of every page shown before sign-in: the page's main landmark, on the app background. */
export const AUTH_PAGE_HOST = {role: 'main', class: 'block min-h-dvh bg-surface-bg'} as const;

/** The top of a page shown before sign-in, inside its `cmn-page`: the app mark, then the lead. */
@Component({
  selector: 'bk-auth-intro',
  imports: [NgOptimizedImage],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {class: 'contents'},
  template: `
    <img ngSrc="/favicon.svg" alt="" width="56" height="56" priority />
    @if (lead()) {
      <p class="text-cmn-sm text-text-secondary">{{ lead() }}</p>
    }
  `,
})
export class AuthIntroComponent {
  public readonly lead = input<string>('');
}

import {NgOptimizedImage} from '@angular/common';
import {ChangeDetectionStrategy, Component, input} from '@angular/core';

/** The frame of the pages shown before sign-in: the mark, a heading, and the content under it. */
@Component({
  selector: 'bk-auth-card',
  imports: [NgOptimizedImage],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {class: 'block min-h-dvh bg-surface-bg'},
  template: `
    <main class="mx-auto flex min-h-dvh max-w-sm flex-col justify-center gap-cmn-6 p-cmn-6">
      <header class="flex flex-col items-center gap-cmn-2 text-center">
        <img ngSrc="/favicon.svg" alt="" width="56" height="56" priority />
        <h1 class="text-cmn-xl font-semibold text-text-primary">{{ heading() }}</h1>
        @if (lead()) {
          <p class="text-cmn-sm text-text-secondary">{{ lead() }}</p>
        }
      </header>
      <ng-content />
    </main>
  `,
})
export class AuthCardComponent {
  public readonly heading = input.required<string>();
  public readonly lead = input<string>('');
}

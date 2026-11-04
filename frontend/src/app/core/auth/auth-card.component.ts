import {ChangeDetectionStrategy, Component, input} from '@angular/core';
import {PageComponent} from '@lifekit-hq/ui';

/** The frame of the pages shown before sign-in: a `cmn-page` titled with the heading, the lead, then the content. */
@Component({
  selector: 'bk-auth-card',
  imports: [PageComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {class: 'block min-h-dvh bg-surface-bg'},
  template: `
    <main>
      <cmn-page [title]="heading()" kind="list">
        @if (lead()) {
          <p class="text-cmn-sm text-text-secondary">{{ lead() }}</p>
        }
        <ng-content />
      </cmn-page>
    </main>
  `,
})
export class AuthCardComponent {
  public readonly heading = input.required<string>();
  public readonly lead = input<string>('');
}

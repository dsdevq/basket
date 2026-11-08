import {ChangeDetectionStrategy, Component} from '@angular/core';

/**
 * The add field and its button, pinned to the bottom of the screen just above the tab bar: the thumb zone, as
 * the bottom toolbar of Apple HIG Toolbars (Reminders' "New Reminder"). It sits last in a `cmn-page` with
 * `fill`, so it rests at the bottom of a short page and sticks above the tab bar on a long one.
 */
@Component({
  selector: 'bk-composer-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'sticky bottom-0 z-10 -mb-cmn-page-gutter mt-auto block bg-surface-bg py-cmn-3',
  },
  template: '<ng-content />',
})
export class ComposerBarComponent {}

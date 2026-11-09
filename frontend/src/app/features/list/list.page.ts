import {DOCUMENT, NgTemplateOutlet} from '@angular/common';
import {ChangeDetectionStrategy, Component, DestroyRef, inject, type OnInit} from '@angular/core';
import {takeUntilDestroyed} from '@angular/core/rxjs-interop';
import {FormControl, FormGroup, ReactiveFormsModule} from '@angular/forms';
import {
  ButtonComponent,
  CheckboxComponent,
  CmnDrawerService,
  EmptyStateComponent,
  InputComponent,
  ListItemRowComponent,
  ListSectionComponent,
  PageComponent,
  SkeletonComponent,
} from '@lifekit-hq/ui';
import {firstValueFrom, fromEvent, interval} from 'rxjs';

import {ComposerBarComponent} from '../../core/shell/composer-bar.component';
import {type ItemAction, ItemActionsSheetComponent} from './item-actions.sheet';
import type {ListItem} from './list.model';
import {ListStore} from './list.store';

/** How often the list re-reads while the page is open and visible, so the other person's changes show. */
const REFRESH_MS = 15_000;

@Component({
  selector: 'bk-list-page',
  imports: [
    NgTemplateOutlet,
    ReactiveFormsModule,
    ComposerBarComponent,
    ButtonComponent,
    CheckboxComponent,
    EmptyStateComponent,
    InputComponent,
    ListItemRowComponent,
    ListSectionComponent,
    PageComponent,
    SkeletonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-page kind="list" fill [reload]="reload">
      @switch (store.status()) {
        @case ('loading') {
          <cmn-skeleton shape="section" />
        }
        @case ('error') {
          <cmn-empty-state
            (action)="store.load()"
            kind="error"
            message="The list did not load"
            subMessage="Check your connection."
            actionLabel="Try again"
          />
        }
        @default {
          @if (!store.items().length) {
            <cmn-empty-state
              icon="ShoppingBasket"
              message="Nothing on the list"
              subMessage="Add the first thing you need. Everyone on the list sees it."
            />
          }
          @if (store.toBuy().length) {
            <cmn-list-section [heading]="'To buy · ' + store.toBuy().length" card>
              @for (item of store.toBuy(); track item.id) {
                <ng-container
                  [ngTemplateOutlet]="row"
                  [ngTemplateOutletContext]="{$implicit: item}"
                />
              }
            </cmn-list-section>
          }
          @if (store.done().length) {
            <cmn-list-section [heading]="'Ticked · ' + store.done().length" card>
              @for (item of store.done(); track item.id) {
                <ng-container
                  [ngTemplateOutlet]="row"
                  [ngTemplateOutletContext]="{$implicit: item}"
                />
              }
            </cmn-list-section>
          }
        }
      }
      <bk-composer-bar>
        <form [formGroup]="form" (ngSubmit)="add()" class="flex items-center gap-cmn-2">
          <cmn-input
            formControlName="text"
            class="min-w-0 flex-1"
            aria-label="Add an item"
            placeholder="Add an item"
            enterkeyhint="done"
            autocomplete="off"
          />
          <cmn-button [loading]="store.adding()" type="submit" variant="primary" icon="Plus"
            >Add</cmn-button
          >
        </form>
      </bk-composer-bar>
    </cmn-page>

    <ng-template #row let-item>
      <cmn-list-item-row
        [label]="item.text"
        [sublabel]="item.addedBy.name"
        [dimmed]="item.checked"
        [swipeLeading]="{
          label: item.checked ? 'Untick' : 'Tick',
          icon: item.checked ? 'Undo' : 'Check',
          tone: 'positive',
        }"
        [swipeTrailing]="{label: 'Delete', icon: 'Trash2', tone: 'danger'}"
        (swipeLeadingAction)="toggle(item, !item.checked)"
        (swipeTrailingAction)="remove(item)"
        class="[&_.truncate]:overflow-visible [&_.truncate]:whitespace-normal [&_.truncate]:break-words"
      >
        <cmn-checkbox
          [checked]="item.checked"
          [label]="(item.checked ? 'Untick ' : 'Tick ') + item.text"
          (changed)="toggle(item, $event)"
          avatar
        />
        <cmn-button
          [aria-label]="'More for ' + item.text"
          (clicked)="more(item)"
          actions
          variant="secondary"
          icon="Ellipsis"
        />
      </cmn-list-item-row>
    </ng-template>
  `,
})
export class ListPage implements OnInit {
  private readonly drawer = inject(CmnDrawerService);
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);

  /** Pull to refresh on this tab root: the shell's pull gesture calls it and waits for the promise. */
  protected readonly reload = (): Promise<void> => this.store.load();

  protected readonly store = inject(ListStore);

  protected readonly text = new FormControl('', {nonNullable: true});
  protected readonly form = new FormGroup({text: this.text});

  public ngOnInit(): void {
    void this.store.load();
    interval(REFRESH_MS)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        if (this.document.visibilityState === 'visible') {
          void this.store.load(true);
        }
      });
    fromEvent(this.document, 'visibilitychange')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        if (this.document.visibilityState === 'visible') {
          void this.store.load(true);
        }
      });
  }

  protected async add(): Promise<void> {
    const value = this.text.value.trim();
    if (!value) {
      return;
    }
    if (await this.store.add(value)) {
      this.text.reset('');
    }
  }

  protected toggle(item: ListItem, checked: boolean): void {
    void this.store.toggle(item, checked);
  }

  protected remove(item: ListItem): void {
    void this.store.remove(item);
  }

  protected async more(item: ListItem): Promise<void> {
    const ref = this.drawer.open<ItemAction, {item: ListItem}>(ItemActionsSheetComponent, {
      title: item.text,
      data: {item},
      mode: 'sheet',
    });
    if ((await firstValueFrom(ref.afterClosed(), {defaultValue: undefined})) === 'delete') {
      this.remove(item);
    }
  }
}

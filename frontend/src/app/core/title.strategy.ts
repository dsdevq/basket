import {inject, Injectable} from '@angular/core';
import {Title} from '@angular/platform-browser';
import {type RouterStateSnapshot, TitleStrategy} from '@angular/router';

/** "Page · basket", the title rule every lifekit app follows. */
@Injectable({providedIn: 'root'})
export class BasketTitleStrategy extends TitleStrategy {
  private readonly title = inject(Title);

  public override updateTitle(snapshot: RouterStateSnapshot): void {
    const page = this.buildTitle(snapshot);
    this.title.setTitle(page ? `${page} · basket` : 'basket');
  }
}

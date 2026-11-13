import { NgTemplateOutlet } from '@angular/common';
import { Component, inject, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { Menu, MenuItem } from './menu';

/** The site's name in the sidebar, the drawer and the phone's top bar; it links to Welcome. */
@Component({
  selector: 'app-brand',
  imports: [RouterLink],
  template: `
    <a routerLink="/" class="flex min-w-0 items-center gap-2.5 rounded-md">
      <span aria-hidden="true" class="grid size-8 shrink-0 place-items-center rounded-md bg-indigo-700 text-xs font-bold tracking-wide text-white">DA</span>
      <span class="min-w-0 leading-tight" [class.max-sm:sr-only]="compact()">
        <span class="block truncate text-sm font-semibold text-slate-900">Dale Abels</span>
        <span class="block truncate text-xs text-slate-500">Demo portfolio</span>
      </span>
    </a>
  `,
})
export class Brand {
  /** Phones with the session widgets beside it: only the monogram shows (the name stays for screen readers). */
  readonly compact = input(false);
}

/**
 * The menu as links, in sections, children indented under their parent with a guide line. App routes are router links that mark the
 * current page (`aria-current="page"`); external addresses are plain links (another host opens in a new tab and says so).
 */
@Component({
  selector: 'app-menu-nav',
  imports: [NgTemplateOutlet, RouterLink, RouterLinkActive],
  template: `
    <nav [attr.aria-label]="label()" class="flex flex-col gap-5 text-sm">
      @for (section of menu.sections(); track $index) {
        <div>
          @if (section.label) {
            <p [id]="idPrefix() + '-section-' + $index" class="px-3 pb-1.5 text-xs font-medium text-slate-500">{{ section.label }}</p>
          }
          <ul class="flex flex-col gap-0.5" [attr.aria-labelledby]="section.label ? idPrefix() + '-section-' + $index : null">
            @for (item of section.items; track item.label + item.url) {
              <li><ng-container *ngTemplateOutlet="entry; context: { $implicit: item }" /></li>
            }
          </ul>
        </div>
      }
    </nav>

    <ng-template #entry let-item>
      @if (!item.url) {
        <span class="side-group">{{ item.label }}</span>
      } @else if (item.external) {
        <a class="side-link" [href]="item.url" [attr.target]="newTab(item) ? '_blank' : null" [attr.rel]="newTab(item) ? 'noopener noreferrer' : null">
          <span class="min-w-0 flex-1 truncate">{{ item.label }}</span>
          @if (newTab(item)) {
            <svg aria-hidden="true" viewBox="0 0 24 24" class="size-3.5 shrink-0 text-slate-400" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5" /></svg>
            <span class="sr-only">(opens in a new tab)</span>
          }
        </a>
      } @else {
        <a
          class="side-link"
          [routerLink]="item.url"
          routerLinkActive="active"
          ariaCurrentWhenActive="page"
          [routerLinkActiveOptions]="{ exact: item.url === '/' || !!item.children?.length }"
          [attr.aria-label]="item.badge ? item.label + ', ' + item.badge + ' waiting' : null"
        >
          <span class="min-w-0 flex-1 truncate">{{ item.label }}</span>
          @if (item.badge) {
            <span class="min-w-5 shrink-0 rounded-full bg-indigo-600 px-1.5 text-center text-xs leading-5 font-semibold text-white">{{ item.badge }}</span>
          }
        </a>
      }
      @if (item.children?.length) {
        <ul class="mt-0.5 ml-3 flex flex-col gap-0.5 border-l border-slate-200 pl-2">
          @for (child of item.children; track child.label + child.url) {
            <li><ng-container *ngTemplateOutlet="entry; context: { $implicit: child }" /></li>
          }
        </ul>
      }
    </ng-template>
  `,
})
export class MenuNav {
  /** The nav's accessible name ("Main" in the sidebar; "Menu" in the drawer). */
  readonly label = input.required<string>();
  /** Keeps the section heading ids unique when two navs are on the page. */
  readonly idPrefix = input('menu');
  protected readonly menu = inject(Menu);

  /** Another host (the Aspire dashboard, an external demo): a new tab. A path of this site (Swagger) loads in place. */
  protected newTab(item: MenuItem): boolean {
    return /^https?:\/\//i.test(item.url);
  }
}

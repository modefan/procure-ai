import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AppConfigService } from '../../../core/config/app-config';
import { opensNewTab } from '../../../core/layout/menu-nav';
import { DIAGRAM_ALT, DIAGRAM_STEPS, TECHNOLOGY } from './technology';

const REPO = 'https://github.com/modefan/procure-ai';

interface Highlight {
  title: string;
  text: string;
}

/**
 * "Procurement demo info" (public, lazy): what the demo does, the architecture diagram (a static SVG drawn from the README's mermaid
 * block at authoring time, so no diagram library is in the bundle), the technology and why, and links. The repository links
 * show only once the repos are public; the dashboard and Swagger links only when the environment configures them.
 */
@Component({
  selector: 'app-about-page',
  imports: [RouterLink],
  template: `
    <article class="mx-auto max-w-4xl px-4 pt-10 pb-16 sm:pt-14">
      <p class="text-sm font-semibold tracking-wide text-indigo-700 uppercase">How it is built</p>
      <h1 class="mt-2 text-3xl font-bold tracking-tight text-slate-900 sm:text-4xl">ProcureAI: the architecture</h1>
      <p class="mt-4 max-w-3xl text-base text-slate-700 sm:text-lg">
        A multi-tenant procurement system in Rand. Employees raise purchase requests, a rules-driven approval chain reserves the budget,
        approved requests become purchase orders for a simulated supplier, deliveries are received, invoices are three-way matched and the
        payment goes to a simulated ERP. An AI assistant answers policy questions with cited sources.
      </p>
      <div class="mt-5 flex flex-wrap gap-3">
        <a routerLink="/demos/procurement" class="btn-primary">Try the demo</a>
      </div>

      <section class="mt-12" aria-labelledby="diagram-title">
        <h2 id="diagram-title" class="text-xl font-semibold text-slate-900">Architecture</h2>
        <figure class="card mt-4 !p-3 sm:!p-5">
          <picture data-testid="architecture-diagram">
            <source media="(max-width: 767px)" srcset="/diagrams/procure-ai-architecture-narrow.svg" />
            <img
              src="/diagrams/procure-ai-architecture.svg"
              [alt]="diagramAlt"
              class="mx-auto h-auto w-full max-w-[56rem]"
              decoding="async"
            />
          </picture>
          <ol class="sr-only" aria-label="The architecture, step by step">
            @for (step of diagramSteps; track step) {
              <li>{{ step }}</li>
            }
          </ol>
          <figcaption class="mt-3 text-center text-sm text-slate-600">
            Arrows show who calls whom or who publishes to whom. Every service has its own database.
          </figcaption>
        </figure>
      </section>

      <section class="mt-12" aria-labelledby="highlights-title">
        <h2 id="highlights-title" class="text-xl font-semibold text-slate-900">Two things worth a closer look</h2>
        <ul class="mt-4 grid gap-4 sm:grid-cols-2">
          @for (highlight of highlights; track highlight.title) {
            <li class="card" data-testid="highlight">
              <h3 class="text-base font-semibold text-slate-900">{{ highlight.title }}</h3>
              <p class="mt-2 text-sm text-slate-600">{{ highlight.text }}</p>
            </li>
          }
        </ul>
      </section>

      <section class="mt-12" aria-labelledby="technology-title">
        <h2 id="technology-title" class="text-xl font-semibold text-slate-900">Technology, and why</h2>
        <div class="mt-4 grid gap-4 md:grid-cols-2">
          @for (group of technology; track group.title) {
            <section class="card" data-testid="technology-group">
              <h3 class="text-base font-semibold text-slate-900">{{ group.title }}</h3>
              <ul class="mt-3 space-y-3">
                @for (item of group.items; track item.name) {
                  <li class="text-sm">
                    <strong class="block font-semibold text-slate-900">{{ item.name }}</strong>
                    <span class="text-slate-600">{{ item.reason }}</span>
                  </li>
                }
              </ul>
            </section>
          }
        </div>
      </section>

      <section class="mt-12" aria-labelledby="links-title">
        <h2 id="links-title" class="text-xl font-semibold text-slate-900">See it for yourself</h2>
        <ul class="mt-4 flex flex-wrap gap-3" data-testid="about-links">
          @if (dashboardUrl()) {
            <li><a [href]="dashboardUrl()" [attr.target]="newTab(dashboardUrl()) ? '_blank' : null" [attr.rel]="newTab(dashboardUrl()) ? 'noopener' : null" class="btn-secondary">Live observability dashboard</a></li>
          }
          @if (swaggerUrl()) {
            <li><a [href]="swaggerUrl()" [attr.target]="newTab(swaggerUrl()) ? '_blank' : null" [attr.rel]="newTab(swaggerUrl()) ? 'noopener' : null" class="btn-secondary">API documentation (Swagger)</a></li>
          }
          @if (reposPublic()) {
            <li><a [href]="repo" target="_blank" rel="noopener" class="btn-secondary">Source code on GitHub</a></li>
            <li><a [href]="repo + '/blob/main/docs/architecture.md'" target="_blank" rel="noopener" class="btn-secondary">Architecture and flows</a></li>
            <li><a [href]="repo + '/blob/main/docs/design-decisions.md'" target="_blank" rel="noopener" class="btn-secondary">Design decisions</a></li>
          }
        </ul>
        @if (!reposPublic()) {
          <p class="mt-3 text-sm text-slate-600" data-testid="source-note">Source code: private for now, available on request.</p>
        }
      </section>
    </article>
  `,
})
export class AboutPage {
  private readonly appConfig = inject(AppConfigService);

  protected readonly repo = REPO;
  /** Same rule as the menu: another host (the dashboard) opens in a new tab, a path of this site (Swagger) in place. */
  protected readonly newTab = opensNewTab;
  protected readonly technology = TECHNOLOGY;
  protected readonly diagramAlt = DIAGRAM_ALT;
  protected readonly diagramSteps = DIAGRAM_STEPS;

  protected readonly dashboardUrl = computed(() => this.appConfig.config().observability.dashboardUrl);
  protected readonly swaggerUrl = computed(() => this.appConfig.config().portfolio.swaggerUrl);
  protected readonly reposPublic = computed(() => this.appConfig.config().portfolio.reposPublic);

  protected readonly highlights: readonly Highlight[] = [
    {
      title: 'A three-layer audit',
      text:
        'Business events are stored by the Audit service. An EF Core interceptor writes a row per changed entity in the same transaction. ' +
        'Debezium reads the database log, and a change with no matching interceptor row was made outside the app and is flagged.',
    },
    {
      title: 'From request to payment',
      text:
        'A request reserves budget, an approval saga routes it with reminders and escalation, and an approved one becomes a purchase order. ' +
        'Delivery and a three-way invoice match lead to payment; cancelling or rejecting at any point releases the budget.',
    },
  ];
}

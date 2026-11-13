import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AppConfigService } from '../../core/config/app-config';
import { SystemAvailability } from '../../core/system/system-availability';

/**
 * The public front door of the portfolio: who Dale is, where to find him, and a card per demo. Everything comes from the runtime
 * `portfolio` config, so a new demo (here or hosted elsewhere) is a config entry. No e-mail or phone, on purpose.
 */
@Component({
  selector: 'app-welcome-page',
  imports: [RouterLink],
  template: `
    <section class="mx-auto max-w-4xl px-4 pt-10 pb-8 sm:pt-16">
      <p class="text-sm font-semibold tracking-wide text-indigo-700 uppercase">Portfolio</p>
      <h1 class="mt-2 text-3xl font-bold tracking-tight text-slate-900 sm:text-5xl">Hi, I'm Dale Abels</h1>
      <p class="mt-5 max-w-2xl text-base text-slate-700 sm:text-lg" data-testid="intro">
        I'm a .NET (C#) and Angular developer who builds microservices. I build the way enterprise teams do: Kubernetes with Argo CD GitOps,
        observability with OpenTelemetry, and security done properly (a BFF, OIDC). This site runs my demos live, so you can try them
        instead of reading about them. I built this site and its demos with Claude Code, Anthropic's AI coding agent: I set the
        architecture, the standards and the review process, and directed every step.
      </p>

      <ul class="mt-6 flex flex-wrap gap-3" aria-label="Find me online" data-testid="profile-links">
        @for (link of links(); track link.url) {
          <li>
            <a [href]="link.url" target="_blank" rel="noopener" class="btn-secondary">{{ link.label }}</a>
          </li>
        }
      </ul>
      @if (!reposPublic()) {
        <p class="mt-3 text-sm text-slate-600" data-testid="source-note">Source code: private for now, available on request.</p>
      }
    </section>

    <section class="mx-auto max-w-4xl px-4 pb-16" aria-labelledby="demos-title">
      <h2 id="demos-title" class="text-lg font-semibold text-slate-900">Live demos</h2>
      @if (paused()) {
        <p class="mt-1 text-sm text-slate-600" role="status" data-testid="demos-paused">
          The demos are paused right now to save hosting costs; you can still read how they are built.
        </p>
      }
      <ul class="mt-4 grid gap-4 sm:grid-cols-2">
        @for (demo of demos(); track demo.key) {
          <li class="card flex flex-col" data-testid="demo-card">
            <h3 class="text-xl font-semibold text-slate-900">{{ demo.name }}</h3>
            <p class="mt-2 grow text-sm text-slate-600">{{ demo.pitch }}</p>
            <div class="mt-5 flex flex-wrap gap-3">
              @if (demo.external) {
                <a [href]="demo.startUrl" target="_blank" rel="noopener" class="btn-primary">Try it</a>
                @if (demo.infoUrl) {
                  <a [href]="demo.infoUrl" target="_blank" rel="noopener" class="btn-secondary">How it is built</a>
                }
              } @else {
                <a [routerLink]="demo.startUrl" class="btn-primary">Try it</a>
                @if (demo.infoUrl) {
                  <a [routerLink]="demo.infoUrl" class="btn-secondary">How it is built</a>
                }
              }
            </div>
          </li>
        }
      </ul>
    </section>
  `,
})
export class WelcomePage {
  private readonly appConfig = inject(AppConfigService);
  private readonly portfolio = computed(() => this.appConfig.config().portfolio);
  protected readonly demos = computed(() => this.portfolio().demos);
  protected readonly links = computed(() => this.portfolio().links);
  protected readonly reposPublic = computed(() => this.portfolio().reposPublic);
  /** The demo is switched off: a calm one-line note; the page itself needs nothing from the server. */
  protected readonly paused = inject(SystemAvailability).disabled;
}

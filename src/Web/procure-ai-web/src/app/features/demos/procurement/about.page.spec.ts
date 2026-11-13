import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AboutPage } from './about.page';
import { AppConfigService } from '../../../core/config/app-config';

describe('AboutPage', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [AboutPage], providers: [provideRouter([])] });
  });

  function render(change: { reposPublic?: boolean; swaggerUrl?: string; dashboardUrl?: string } = {}) {
    const config = TestBed.inject(AppConfigService).config;
    config.set({
      ...config(),
      observability: { ...config().observability, dashboardUrl: change.dashboardUrl ?? '' },
      portfolio: {
        ...config().portfolio,
        reposPublic: change.reposPublic ?? false,
        swaggerUrl: change.swaggerUrl ?? '',
      },
    });
    const fixture = TestBed.createComponent(AboutPage);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const link = (element: HTMLElement, text: string) =>
    Array.from(element.querySelectorAll('a')).find((a) => a.textContent?.trim() === text);

  it('about_shows_diagram_with_text_alternative', () => {
    const element = render();

    const picture = element.querySelector('picture[data-testid="architecture-diagram"]');
    expect(picture).not.toBeNull();
    const source = picture!.querySelector('source');
    expect(source?.getAttribute('media')).toBe('(max-width: 767px)');
    expect(source?.getAttribute('srcset')).toBe('/diagrams/procure-ai-architecture-narrow.svg');
    const image = picture!.querySelector('img');
    expect(image?.getAttribute('src')).toBe('/diagrams/procure-ai-architecture.svg');
    const alt = image?.getAttribute('alt') ?? '';
    for (const part of ['Angular', 'gateway', 'Keycloak', 'RabbitMQ', 'Postgres', 'MongoDB', 'Debezium', 'Audit', 'Redis']) {
      expect(alt.toLowerCase()).toContain(part.toLowerCase());
    }
    const description = element.querySelector('ol.sr-only');
    expect(description).not.toBeNull();
    expect(description!.querySelectorAll('li').length).toBeGreaterThanOrEqual(5);
  });

  it('about_says_it_was_built_with_claude_code', () => {
    const credit = render().querySelector('[data-testid="built-with"]')?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

    expect(credit).toContain('Claude Code');
    expect(credit).toBe(
      'Built with Claude Code: planned, written, tested and reviewed by AI agents under my direction, one reviewed task at a time.',
    );
  });

  it('about_lists_technology_groups', () => {
    const element = render();

    const groups = Array.from(element.querySelectorAll('[data-testid="technology-group"]'));
    expect(groups.map((g) => g.querySelector('h3')?.textContent?.trim())).toEqual([
      'Backend',
      'Messaging and data',
      'Frontend',
      'AI',
      'Delivery and operations',
    ]);
    for (const group of groups) {
      const items = group.querySelectorAll('li');
      expect(items.length).toBeGreaterThan(0);
      for (const item of Array.from(items)) {
        expect(item.querySelector('strong')?.textContent?.trim()).toBeTruthy();
        expect(item.querySelector('span')?.textContent?.trim().length).toBeGreaterThan(10);
      }
    }
    expect(element.textContent).toContain('Wolverine');
    expect(element.textContent).toContain('Debezium');
    expect(element.textContent).toContain("Marten stores the Audit service's events in Postgres");
  });

  it('about_links_dashboard_and_swagger', () => {
    const element = render({ dashboardUrl: 'https://dash.example/', swaggerUrl: '/swagger' });

    expect(link(element, 'Live observability dashboard')?.getAttribute('href')).toBe('https://dash.example/');
    expect(link(element, 'API documentation (Swagger)')?.getAttribute('href')).toBe('/swagger');
    // Like the menu: the dashboard (another host) and Swagger both open in a new tab.
    expect(link(element, 'Live observability dashboard')?.getAttribute('target')).toBe('_blank');
    expect(link(element, 'API documentation (Swagger)')?.getAttribute('target')).toBe('_blank');
  });

  it('about_opens_an_absolute_swagger_address_in_a_new_tab_like_the_menu', () => {
    const element = render({ swaggerUrl: 'https://api.example/swagger' });

    expect(link(element, 'API documentation (Swagger)')?.getAttribute('target')).toBe('_blank');
  });

  it('about_hides_dashboard_and_swagger_links_when_not_configured', () => {
    const element = render();

    expect(link(element, 'Live observability dashboard')).toBeUndefined();
    expect(link(element, 'API documentation (Swagger)')).toBeUndefined();
  });

  it('about_links_repo_only_when_public', () => {
    const hidden = render({ reposPublic: false });
    expect(hidden.querySelector('a[href^="https://github.com/modefan/procure-ai"]')).toBeNull();
    expect(hidden.textContent).toContain('private for now');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ imports: [AboutPage], providers: [provideRouter([])] });

    const shown = render({ reposPublic: true });
    expect(link(shown, 'Source code on GitHub')?.getAttribute('href')).toBe('https://github.com/modefan/procure-ai');
    expect(link(shown, 'Architecture and flows')?.getAttribute('href')).toBe('https://github.com/modefan/procure-ai/blob/main/docs/architecture.md');
    expect(link(shown, 'Design decisions')?.getAttribute('href')).toBe('https://github.com/modefan/procure-ai/blob/main/docs/design-decisions.md');
    expect(shown.textContent).not.toContain('private for now');
  });
});

import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { WelcomePage } from './welcome.page';
import { AppConfigService } from '../../core/config/app-config';
import { SystemAvailability } from '../../core/system/system-availability';

describe('WelcomePage', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [WelcomePage], providers: [provideRouter([])] });
  });

  function render() {
    const fixture = TestBed.createComponent(WelcomePage);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('welcome_shows_name_profile_links_and_a_card_per_demo', () => {
    const element = render();

    expect(element.querySelector('h1')?.textContent).toContain("Hi, I'm Dale Abels");
    const links = Array.from(element.querySelectorAll<HTMLAnchorElement>('[data-testid="profile-links"] a'));
    expect(links.map((a) => [a.textContent?.trim(), a.getAttribute('href'), a.getAttribute('rel')])).toEqual([
      ['LinkedIn', 'https://www.linkedin.com/in/dale-abels-95918154/', 'noopener'],
      ['GitHub', 'https://github.com/modefan', 'noopener'],
    ]);

    const cards = element.querySelectorAll('[data-testid="demo-card"]');
    expect(cards).toHaveLength(1);
    expect(cards[0].textContent).toContain('ProcureAI');
    expect(cards[0].textContent).toContain('Procurement in Rand with an AI policy assistant');
    const tryIt = Array.from(cards[0].querySelectorAll('a')).find((a) => a.textContent?.trim() === 'Try it');
    const info = Array.from(cards[0].querySelectorAll('a')).find((a) => a.textContent?.trim() === 'How it is built');
    expect(tryIt?.getAttribute('href')).toBe('/demos/procurement');
    expect(info?.getAttribute('href')).toBe('/demos/procurement/about');
  });

  it('shows a card for every configured demo, an external one as a new-tab link', () => {
    const config = TestBed.inject(AppConfigService).config;
    config.set({
      ...config(),
      portfolio: {
        ...config().portfolio,
        demos: [
          ...config().portfolio.demos,
          { key: 'other', name: 'Other', pitch: 'Elsewhere', startUrl: 'https://other.example/', infoUrl: '', external: true },
        ],
      },
    });
    const element = render();

    const cards = element.querySelectorAll('[data-testid="demo-card"]');
    expect(cards).toHaveLength(2);
    const links = Array.from(cards[1].querySelectorAll('a'));
    expect(links.map((a) => a.textContent?.trim())).toEqual(['Try it']);
    expect(links[0].getAttribute('href')).toBe('https://other.example/');
    expect(links[0].getAttribute('rel')).toBe('noopener');
  });

  it('welcome_shows_private_source_text_until_repos_public', () => {
    expect(render().textContent).toContain('Source code: private for now, available on request');

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ imports: [WelcomePage], providers: [provideRouter([])] });
    const config = TestBed.inject(AppConfigService).config;
    config.set({ ...config(), portfolio: { ...config().portfolio, reposPublic: true } });
    expect(render().textContent).not.toContain('private for now');
  });

  it('welcome_shows_a_paused_note_only_while_the_demo_is_switched_off', () => {
    expect(render().querySelector('[data-testid="demos-paused"]')).toBeNull();

    TestBed.inject(SystemAvailability).markDisabled(null);
    const element = render();

    expect(element.querySelector('[data-testid="demos-paused"]')?.textContent).toContain('paused');
    expect(element.querySelectorAll('[data-testid="demo-card"]')).toHaveLength(1);
  });

  it('welcome_has_no_email_or_phone', () => {
    const element = render();

    expect(element.querySelector('a[href^="mailto:"]')).toBeNull();
    expect(element.querySelector('a[href^="tel:"]')).toBeNull();
    expect(element.innerHTML).not.toMatch(/mailto:|tel:|@[a-z0-9-]+\.[a-z]{2,}/i);
  });
});

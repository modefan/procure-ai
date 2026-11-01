import { TestBed } from '@angular/core/testing';
import { convertToParamMap } from '@angular/router';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { AccessService } from '../../../api/access/services/access.service';
import { AdminConversationsService } from '../../../api/assistant/services/admin-conversations.service';
import { userDto } from '../admin.testing';
import { conversationDto, conversationItem } from './conversations.testing';
import { ConversationPage } from './conversation.page';
import { ConversationsPage } from './conversations.page';

describe('conversation viewer', () => {
  let api: Record<'adminListConversations' | 'adminGetConversation', ReturnType<typeof vi.fn>>;
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    for (let i = 0; i < 4; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
  }

  beforeEach(() => {
    api = {
      adminListConversations: vi.fn().mockResolvedValue({ items: [conversationItem('c1'), conversationItem('c2', { userId: 'ben', title: 'Cloud hosting policy' })], page: 1, pageSize: 25, total: 2 }),
      adminGetConversation: vi.fn().mockResolvedValue(conversationDto('c1')),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AdminConversationsService, useValue: api },
        { provide: AccessService, useValue: { listUsers: vi.fn().mockResolvedValue([userDto('tom', { displayName: 'Tom Mokoena' }), userDto('ben', { displayName: 'Ben Buyer' })]) } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'c1' }) } } },
      ],
    });
  });

  it('the list shows user, title and last activity and filters by user', async () => {
    const fixture = TestBed.createComponent(ConversationsPage);
    fixture.detectChanges();
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el.querySelector('[data-conversation="c2"]'))).toContain('Cloud hosting policy');
    expect(text(el.querySelector('[data-conversation="c2"]'))).toContain('Ben Buyer');
    expect(text(el.querySelector('[data-conversation="c1"]'))).toContain('Tom Mokoena');
    expect(text(el.querySelector('[data-conversation="c1"]'))).toContain('4 messages');
    expect(text(el.querySelector('[data-conversation="c1"]'))).toContain('1 blocked');
    expect(el.querySelector('[data-conversation="c1"] a')?.getAttribute('href')).toBe('/app/admin/conversations/c1');
    const filter = el.querySelector<HTMLSelectElement>('#conversation-user')!;
    filter.value = 'ben';
    filter.dispatchEvent(new Event('change'));
    await settle(fixture);
    expect(api.adminListConversations).toHaveBeenLastCalledWith({ userId: 'ben', page: 1, pageSize: 25 });
  });

  it('conversation_detail_shows_tool_steps_and_flags', async () => {
    const fixture = TestBed.createComponent(ConversationPage);
    fixture.detectChanges();
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(api.adminGetConversation).toHaveBeenCalledWith({ id: 'c1' });
    expect(el.querySelectorAll('[data-testid="admin-message"]').length).toBe(4);
    expect(text(el.querySelector('[data-tool="explain_approval_chain"]'))).toContain('Worked out the approvers');
    expect(text(el.querySelector('[data-source="S1"]'))).toContain('Procurement Policy');
    expect(text(el)).toContain('Tom Mokoena');
    expect(text(el)).toContain('812 in');
    expect(text(el)).toContain('64 out');
    expect(text(el)).toContain('gpt-4.1-mini');
    expect(text(el.querySelector('[data-testid="guard-flags"]'))).toContain('prompt-injection');
    expect(text(el)).toContain('Blocked');
    // read-only: no form, no input, no send or edit controls
    expect(el.querySelector('textarea, input, form')).toBeNull();
  });
});

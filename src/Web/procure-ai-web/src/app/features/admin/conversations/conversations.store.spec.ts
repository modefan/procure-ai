import { TestBed } from '@angular/core/testing';
import { AdminConversationsService } from '../../../api/assistant/services/admin-conversations.service';
import { conversationDto, conversationItem } from './conversations.testing';
import { ConversationsStore } from './conversations.store';

describe('ConversationsStore', () => {
  let api: Record<'adminListConversations' | 'adminGetConversation', ReturnType<typeof vi.fn>>;
  const store = () => TestBed.inject(ConversationsStore);

  beforeEach(() => {
    api = {
      adminListConversations: vi.fn().mockResolvedValue({ items: [conversationItem('c1'), conversationItem('c2', { userId: 'ben' })], page: 1, pageSize: 25, total: 2 }),
      adminGetConversation: vi.fn().mockResolvedValue(conversationDto('c1')),
    };
    TestBed.configureTestingModule({ providers: [{ provide: AdminConversationsService, useValue: api }] });
  });

  it('loads a page and filters by user', async () => {
    await store().load();
    expect(api.adminListConversations).toHaveBeenCalledWith({ userId: undefined, page: 1, pageSize: 25 });
    expect(store().items().length).toBe(2);
    await store().setUser('ben');
    expect(api.adminListConversations).toHaveBeenLastCalledWith({ userId: 'ben', page: 1, pageSize: 25 });
  });

  it('counts blocked answers and tool steps of a detail', async () => {
    await store().loadDetail('c1');
    expect(store().detail()?.id).toBe('c1');
    expect(store().blockedCount()).toBe(1);
  });

  it('a failed load is reported', async () => {
    api.adminListConversations.mockRejectedValue(new Error('x'));
    await store().load();
    expect(store().error()).toBeTruthy();
  });
});

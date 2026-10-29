import { TestBed } from '@angular/core/testing';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { LiveConnection } from '../../core/live/live-connection';
import { invoiceDto } from '../requests/request-detail.testing';
import { InvoicesStore } from './invoices.store';
import { invoicesPage } from './invoices.testing';

describe('InvoicesStore', () => {
  let listInvoices: ReturnType<typeof vi.fn>;
  let fake: ReturnType<typeof fakeHubFactory>;

  beforeEach(() => {
    listInvoices = vi.fn().mockResolvedValue(invoicesPage([invoiceDto()]));
    fake = fakeHubFactory();
    TestBed.configureTestingModule({ providers: [{ provide: InvoicesService, useValue: { listInvoices } }, { provide: LIVE_HUB_FACTORY, useValue: fake.factory }] });
  });
  const store = () => TestBed.inject(InvoicesStore);

  it('loads a page through the generated client', async () => {
    await store().load(1);
    expect(listInvoices).toHaveBeenCalledWith({ page: 1, pageSize: 25 });
    expect(store().entities()).toHaveLength(1);
    expect(store().total()).toBe(1);
  });

  it('a status filter is sent and shows page 1 of the result', async () => {
    await store().setStatus('Mismatched');
    expect(listInvoices).toHaveBeenLastCalledWith({ page: 1, pageSize: 25, status: 'Mismatched' });
    await store().setStatus('all');
    expect(listInvoices).toHaveBeenLastCalledWith({ page: 1, pageSize: 25 });
  });

  it('a failure sets the error and keeps the rows', async () => {
    await store().load(1);
    listInvoices.mockRejectedValue(new Error('down'));
    store().invalidate('list');
    await store().load(1);
    await vi.waitFor(() => expect(store().error()).toBe('The invoices could not be loaded.'));
    expect(store().entities()).toHaveLength(1);
  });

  it('entity_changed_revalidates: an invoice push refreshes the page on screen in the background', async () => {
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    TestBed.inject(InvoicesStore);
    await store().load(1);
    listInvoices.mockResolvedValue(invoicesPage([invoiceDto(), invoiceDto({ id: 'i2', supplierInvoiceNumber: 'INV-78' })]));
    fake.hubs[0].emit('EntityChanged', { entity: 'invoice', id: 'i2', requestId: 'r1', eventType: 'InvoiceReceived', occurredAt: '2026-10-29T10:00:00Z' });
    await vi.waitFor(() => expect(store().entities()).toHaveLength(2));
    expect(store().isLoading()).toBe(false);
    live.stop();
  });
});

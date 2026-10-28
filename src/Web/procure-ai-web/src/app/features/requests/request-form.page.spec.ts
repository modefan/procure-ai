import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { AccessService } from '../../api/access/services/access.service';
import { ApprovalsService } from '../../api/approvals/services/approvals.service';
import { BudgetsService } from '../../api/budgets/services/budgets.service';
import { RequestDto } from '../../api/requests/models/request-dto';
import { RequestsService } from '../../api/requests/services/requests.service';
import { SuppliersService } from '../../api/suppliers/services/suppliers.service';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { RequestFormPage } from './request-form.page';

const dto = (o: Partial<RequestDto> = {}): RequestDto => ({
  id: 'r1', number: 'PR-1001', title: 'Laptops', status: 'Draft', costCenterCode: 'IT-200', supplierId: 'CH',
  lines: [{ lineNo: 1, sku: 'L14', description: 'Laptop 14"', quantity: 3, unitPriceExclVat: 25200, unitPriceExclVatDisplay: 'R 25 200,00', totalExclVat: 75600, totalExclVatDisplay: 'R 75 600,00' }],
  submittedAt: null, approvedAt: null, rejectedAt: null, rejectionReason: null, cancelledAt: null,
  cancellationReason: null, orderId: null, orderNumber: null, spentInclVat: null, spentInclVatDisplay: null, cancellationRefusedReason: null,
  totalExclVat: 75600, totalExclVatDisplay: 'R 75 600,00', vat: 11340, vatDisplay: 'R 11 340,00', totalInclVat: 86940, totalInclVatDisplay: 'R 86 940,00',
  version: 1, ...o,
});

const catalogItem = (sku: string, description: string, price: number) => ({ sku, description, unit: 'each', active: true, unitPriceExclVat: price, unitPriceDisplay: `R ${price}` });

describe('RequestFormPage', () => {
  let api: Record<'createRequest' | 'updateRequest' | 'submitRequest' | 'getRequest', ReturnType<typeof vi.fn>>;
  let checkBudget: ReturnType<typeof vi.fn>;
  let previewApproval: ReturnType<typeof vi.fn>;

  async function setup(id: string | null = null, loaded: RequestDto | HttpErrorResponse = dto()) {
    api = {
      createRequest: vi.fn().mockResolvedValue(dto()),
      updateRequest: vi.fn().mockResolvedValue(dto({ version: 2 })),
      submitRequest: vi.fn().mockResolvedValue(dto({ status: 'Submitted', version: 3 })),
      getRequest: loaded instanceof HttpErrorResponse ? vi.fn().mockRejectedValue(loaded) : vi.fn().mockResolvedValue(loaded),
    };
    checkBudget = vi.fn().mockResolvedValue({ available: 266400, fiscalYear: '2026', fits: true });
    previewApproval = vi.fn().mockResolvedValue({ possible: true, reason: null, steps: [{ stepNo: 1, level: 'Manager', approverNames: ['Maria'] }] });
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: '**', children: [] }]),
        { provide: RequestsService, useValue: api },
        { provide: BudgetsService, useValue: { checkBudget } },
        { provide: ApprovalsService, useValue: { previewApproval } },
        { provide: ConflictDialog, useValue: { show: vi.fn().mockResolvedValue(undefined) } },
        {
          provide: AccessService,
          useValue: { getMe: vi.fn().mockResolvedValue({ userId: 'u', name: 'Tom', roles: [], approvalLimit: null, costCenters: [{ code: 'IT-200', name: 'IT', departmentCode: 'IT' }] }) },
        },
        {
          provide: SuppliersService,
          useValue: {
            listSuppliers: vi.fn().mockResolvedValue({ items: [{ code: 'CH', name: 'Contoso Hardware', active: true, itemCount: 2 }], page: 1, pageSize: 100, total: 1 }),
            searchCatalog: vi.fn().mockResolvedValue({ items: [catalogItem('L14', 'Laptop 14"', 25200), catalogItem('MS', 'Mouse', 300)], page: 1, pageSize: 100, total: 2 }),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(RequestFormPage);
    if (id) {
      fixture.componentRef.setInput('id', id);
    }
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance };
  }

  async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    for (let i = 0; i < 3; i++) {
      await fixture.whenStable();
      fixture.detectChanges();
    }
  }

  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const set = (input: HTMLInputElement | HTMLSelectElement, value: string) => {
    input.value = value;
    input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input', { bubbles: true }));
    input.dispatchEvent(new Event('input', { bubbles: true }));
  };
  const field = (el: HTMLElement, id: string) => el.querySelector(`#${id}`) as HTMLInputElement & HTMLSelectElement;
  const click = (el: Element | null) => (el as HTMLElement).click();

  async function fillValid(ctx: Awaited<ReturnType<typeof setup>>) {
    set(field(ctx.el, 'title'), 'Laptops');
    set(field(ctx.el, 'supplier'), 'CH');
    await settle(ctx.fixture);
    set(field(ctx.el, 'cost-center'), 'IT-200');
    set(ctx.el.querySelector('select[aria-label="Item, line 1"]') as HTMLSelectElement, 'L14');
    set(ctx.el.querySelector('input[aria-label="Quantity, line 1"]') as HTMLInputElement, '3');
    await settle(ctx.fixture);
  }

  it('totals_and_vat_on_document_total: 3 x R 25 200,00 shows R 75 600,00, VAT R 11 340,00 and R 86 940,00', async () => {
    const ctx = await setup();
    await fillValid(ctx);

    const totals = text(ctx.el.querySelector('[data-testid="totals"]'));
    expect(totals).toContain('R 75 600,00');
    expect(totals).toContain('R 11 340,00');
    expect(totals).toContain('R 86 940,00');
    expect(text(ctx.el.querySelector('[data-testid="line-0"]'))).toContain('R 25 200,00');
  });

  it('shows a ZAR budget verdict from the check, over budget with the shortfall', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      checkBudget = vi.fn();
      const ctx = await setup();
      checkBudget.mockResolvedValue({ available: 50000, fiscalYear: '2026', fits: false });
      await fillValid(ctx);
      await vi.advanceTimersByTimeAsync(400);
      await settle(ctx.fixture);

      expect(text(ctx.el.querySelector('[data-testid="budget"]'))).toContain('Over budget by R 36 940,00');
    } finally {
      vi.useRealTimers();
    }
  });

  it('server_field_errors_shown_under_fields, and general errors in an alert', async () => {
    const ctx = await setup();
    await fillValid(ctx);
    api.createRequest.mockRejectedValueOnce(
      new HttpErrorResponse({ status: 400, error: { title: 'invalid', errors: { Title: ['Title already used.'], 'Lines[0].Quantity': ['Not enough stock.'] } } }),
    );

    click(ctx.el.querySelector('[data-testid="save-draft"] button'));
    await settle(ctx.fixture);

    expect(text(ctx.el.querySelector('#title-error'))).toBe('Title already used.');
    expect(text(ctx.el.querySelector('#line-0-quantity-error'))).toBe('Not enough stock.');
    expect(ctx.el.querySelector('[role="alert"]')).toBeNull();

    api.createRequest.mockRejectedValueOnce(new HttpErrorResponse({ status: 422, error: { title: 'x', detail: 'The supplier is not active.' } }));
    click(ctx.el.querySelector('[data-testid="save-draft"] button'));
    await settle(ctx.fixture);
    expect(text(ctx.el.querySelector('[role="alert"]'))).toContain('The supplier is not active.');
  });

  it('client validation shows messages and sends nothing', async () => {
    const ctx = await setup();

    click(ctx.el.querySelector('[data-testid="submit"] button'));
    await settle(ctx.fixture);

    expect(api.createRequest).not.toHaveBeenCalled();
    expect(text(ctx.el.querySelector('#title-error'))).toBe('Enter a title');
    expect(text(ctx.el.querySelector('#supplier-error'))).toBe('Choose a supplier');
  });

  it('submit saves, submits once for a double click, and goes to the request', async () => {
    const ctx = await setup();
    await fillValid(ctx);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const button = ctx.el.querySelector('[data-testid="submit"] button') as HTMLButtonElement;

    button.click();
    button.click();
    await settle(ctx.fixture);

    expect(api.submitRequest).toHaveBeenCalledOnce();
    expect(navigate).toHaveBeenCalledWith(['/app/requests', 'r1']);
  });

  it('unsaved_guard_asks_only_when_dirty: the page reports edits until they are saved', async () => {
    const ctx = await setup();
    expect(ctx.page.hasUnsavedChanges()).toBe(false);

    set(field(ctx.el, 'title'), 'Something');
    await settle(ctx.fixture);
    expect(ctx.page.hasUnsavedChanges()).toBe(true);

    await fillValid(ctx);
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    click(ctx.el.querySelector('[data-testid="save-draft"] button'));
    await settle(ctx.fixture);
    expect(ctx.page.hasUnsavedChanges()).toBe(false);
  });

  it('blocks Submit and says why when no approver exists for the total', async () => {
    const ctx = await setup();
    previewApproval.mockResolvedValue({ possible: false, reason: 'No approver for R 900 000,00.', steps: [] });
    await fillValid(ctx);
    await new Promise((r) => setTimeout(r, 450));
    await settle(ctx.fixture);

    expect(text(ctx.el.querySelector('[data-testid="approvers"]'))).toContain('No approver for R 900 000,00.');
    expect((ctx.el.querySelector('[data-testid="submit"] button') as HTMLButtonElement).disabled).toBe(true);
  });

  it('lists approval steps with names', async () => {
    const ctx = await setup();
    await fillValid(ctx);
    await new Promise((r) => setTimeout(r, 450));
    await settle(ctx.fixture);

    expect(text(ctx.el.querySelector('[data-testid="approvers"]'))).toContain('1. Manager (Maria)');
  });

  it('edits a draft: loads it, keeps supplier and cost center fixed, saves with the line changes', async () => {
    const ctx = await setup('r1');

    await vi.waitFor(() => {
      ctx.fixture.detectChanges();
      expect(field(ctx.el, 'title').value).toBe('Laptops');
    });
    expect(field(ctx.el, 'supplier').disabled).toBe(true);
    expect(text(ctx.el)).toContain('Edit PR-1001');
    expect(ctx.page.hasUnsavedChanges()).toBe(false);

    set(ctx.el.querySelector('input[aria-label="Quantity, line 1"]') as HTMLInputElement, '4');
    await settle(ctx.fixture);
    click(ctx.el.querySelector('[data-testid="save-draft"] button'));
    await settle(ctx.fixture);

    expect(api.updateRequest).toHaveBeenCalledWith({ id: 'r1', 'If-Match': '"1"', body: { title: 'Laptops', lines: [{ sku: 'L14', quantity: 4 }] } });
  });

  it('refuses to edit a request that is no longer a draft', async () => {
    const ctx = await setup('r1', dto({ status: 'Submitted' }));

    expect(text(ctx.el)).toContain('Only drafts can be edited');
    expect(ctx.el.querySelector('form')).toBeNull();
  });

  it('shows a failed load with a way back instead of an empty form', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const ctx = await setup('r1', new HttpErrorResponse({ status: 404, error: { title: 'Request not found.' } }));

    expect(text(ctx.el.querySelector('[role="alert"]'))).toContain('Request not found.');
    expect(ctx.el.querySelector('form')).toBeNull();
  });
});

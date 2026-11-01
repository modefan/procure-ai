import { BudgetDto } from '../../api/budgets/models/budget-dto';

/** The wireframe's IT-200: R 900 000,00 budget, R 558 000,00 spent, R 75 600,00 reserved, R 266 400,00 available. */
export const budgetDto = (code = 'IT-200', o: Partial<BudgetDto> = {}): BudgetDto => ({
  costCenterCode: code, fiscalYear: 'FY2027', amount: 900000, spent: 558000, reserved: 75600, available: 266400, version: 3, ownerName: 'Maria Naidoo', ...o,
});

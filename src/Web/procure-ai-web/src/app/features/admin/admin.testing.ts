import { AuthorizationCheckDto } from '../../api/approvals/models/authorization-check-dto';
import { MatrixDto } from '../../api/access/models/matrix-dto';
import { CostCenterAdminDto } from '../../api/access/models/cost-center-admin-dto';
import { UserDto } from '../../api/access/models/user-dto';

/** The 15 permissions of the system, in the order the Access service lists them. */
export const ALL_PERMISSIONS = [
  'requests.read',
  'requests.create',
  'requests.approve',
  'orders.read',
  'orders.receive',
  'invoices.read',
  'invoices.create',
  'invoices.release-payment',
  'budgets.read',
  'budgets.manage',
  'assistant.use',
  'admin.authorization',
  'admin.users',
  'admin.policies',
  'admin.system',
];

/** The demo seed's matrix (version 4): five roles, the admin keeps `admin.authorization`. */
export const matrixDto = (o: Partial<MatrixDto> = {}): MatrixDto => ({
  tenantId: 'acme',
  version: 4,
  availablePermissions: ALL_PERMISSIONS,
  roles: [
    { role: 'requester', permissions: ['requests.read', 'requests.create', 'orders.read', 'assistant.use'] },
    { role: 'manager', permissions: ['requests.read', 'requests.create', 'requests.approve', 'orders.read', 'budgets.read', 'assistant.use'] },
    { role: 'department-head', permissions: ['requests.read', 'requests.approve', 'orders.read', 'budgets.read', 'assistant.use'] },
    {
      role: 'finance',
      permissions: ['requests.read', 'orders.read', 'orders.receive', 'invoices.read', 'invoices.create', 'invoices.release-payment', 'budgets.read', 'budgets.manage', 'assistant.use'],
    },
    { role: 'admin', permissions: ['requests.read', 'budgets.read', 'assistant.use', 'admin.authorization', 'admin.users', 'admin.policies', 'admin.system'] },
  ],
  ...o,
});

/** A user of the Users screen as the Access service lists them. */
export const userDto = (userId: string, o: Partial<UserDto> = {}): UserDto => ({
  userId,
  displayName: 'Maria Naidoo',
  email: 'maria@acme.test',
  roles: ['manager'],
  costCenterCodes: ['IT-200'],
  approvalLimit: 200000,
  isFinanceApprover: false,
  version: 3,
  ...o,
});

export const costCenterAdminDto = (code: string, o: Partial<CostCenterAdminDto> = {}): CostCenterAdminDto => ({
  code,
  name: 'IT Operations',
  departmentCode: 'IT',
  departmentName: 'Information Technology',
  managerId: 'maria',
  managerName: 'Maria Naidoo',
  departmentHeadId: 'hendrik',
  departmentHeadName: 'Hendrik Head',
  ...o,
});

/** An answer of the authorization check: a denied approval (separation of duties) with one routing explanation line. */
export const checkDto = (o: Partial<AuthorizationCheckDto> = {}): AuthorizationCheckDto => ({
  allowed: false,
  action: 'approve',
  userName: 'Maria Naidoo',
  requestNumber: 'PR-1039',
  steps: [
    { code: 'permission', passed: true, text: 'Has permission requests.approve', gating: true },
    { code: 'separation', passed: false, text: 'Is not the requester of this request', gating: true },
    { code: 'cost-center', passed: false, text: 'Routing today would not pick this user for cost center IT-200', gating: false },
  ],
  ...o,
});

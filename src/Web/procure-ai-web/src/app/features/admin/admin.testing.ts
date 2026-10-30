import { MatrixDto } from '../../api/access/models/matrix-dto';

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

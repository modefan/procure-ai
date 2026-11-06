const ROLE_LABELS: Record<string, string> = {
  requester: 'Requester',
  manager: 'Manager',
  'department-head': 'Department head',
  finance: 'Finance',
  admin: 'Admin',
};

/** The screen name of a role (`department-head` becomes "Department head"): the matrix, the users screen and the header's user menu. */
export function roleLabel(role: string): string {
  return ROLE_LABELS[role] ?? role.charAt(0).toUpperCase() + role.slice(1).replace(/-/g, ' ');
}

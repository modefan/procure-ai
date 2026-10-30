import { PolicyDocumentDto } from '../../api/assistant/models/policy-document-dto';

/** A ready document: the demo seed's procurement policy. */
export const policyDto = (id = 'doc-1', o: Partial<PolicyDocumentDto> = {}): PolicyDocumentDto => ({
  id,
  title: 'Procurement Policy 2026',
  fileName: 'procurement-policy-2026.pdf',
  version: 3,
  currentVersion: 3,
  status: 'Ready',
  progress: 100,
  chunkCount: 148,
  uploadedAt: '2026-10-20T08:00:00Z',
  uploadedByName: 'Admin',
  flaggedReason: null,
  failureReason: null,
  rowVersion: 7,
  ...o,
});

export const fileOf = (name: string, bytes = 10, type = 'application/pdf'): File => new File([new Uint8Array(bytes)], name, { type });

import { DataChangeDto } from '../../../api/audit/models/data-change-dto';

/** An update of a purchase request's title and version, made by tom through the app. */
export const dataChangeDto = (id: string, o: Partial<DataChangeDto> = {}): DataChangeDto => ({
  id,
  database: 'requestsdb',
  table: 'purchase_requests',
  primaryKey: '5b1f0c2e-0000-4000-8000-000000000001',
  operation: 'Update',
  fields: [
    { column: 'title', old: '3 laptops', new: '4 laptops' },
    { column: 'version', old: '5', new: '6' },
  ],
  actorId: 'tom',
  userId: 'tom',
  tenantId: 'acme',
  subjectId: '5b1f0c2e-0000-4000-8000-000000000001',
  attribution: 'App',
  callingService: null,
  committedAt: '2026-10-30T08:15:30Z',
  correlationId: 'corr-1',
  transactionId: 901,
  ...o,
});

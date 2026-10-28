/** A user seen online or viewing an entity. */
export interface Viewer {
  userId: string;
  name: string;
}

/** What the hub pushes when something changed: IDs only, the client refetches through the authorized APIs. */
export interface EntityChanged {
  /** `request`, `approval`, `budget`, `order` or `invoice`. */
  entity: string;
  id: string;
  requestId: string | null;
  eventType: string;
  occurredAt: string;
}

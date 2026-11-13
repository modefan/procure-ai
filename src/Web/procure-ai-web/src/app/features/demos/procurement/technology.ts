export interface TechnologyItem {
  name: string;
  /** One line on why it was chosen. */
  reason: string;
}

export interface TechnologyGroup {
  title: string;
  items: readonly TechnologyItem[];
}

/** The technology the Procurement demo uses and why (from the repository's README "What to look at" and docs/design-decisions.md). */
export const TECHNOLOGY: readonly TechnologyGroup[] = [
  {
    title: 'Backend',
    items: [
      { name: '.NET 10 microservices, Clean Architecture', reason: 'Four projects per service (Domain, Application, Infrastructure, Api); architecture tests fail the build when a layer rule is broken.' },
      { name: 'YARP gateway with a BFF login', reason: 'The browser only holds an HttpOnly session cookie; tokens stay on the server and never reach the browser.' },
      { name: 'Keycloak (OIDC) and policy-based authorization', reason: 'Roles map to permissions at runtime, with resource rules such as separation of duties; every service validates the JWT itself.' },
      { name: 'Idempotent writes and request coalescing', reason: 'Identical concurrent requests run once across instances, so a double click or a retry never creates two requests.' },
      { name: 'Resilience on every outgoing call', reason: 'Circuit breakers, retries with backoff and timeouts keep one slow dependency from taking the rest down.' },
    ],
  },
  {
    title: 'Messaging and data',
    items: [
      { name: 'Wolverine over RabbitMQ', reason: 'Transactional outbox and inbox, and an orchestrated approval saga with timeouts, escalation and compensation (MIT licensed, unlike MassTransit v9).' },
      { name: 'Postgres with pgvector', reason: 'One database per service; relational data, jsonb documents and vector search for the AI assistant in one engine.' },
      { name: 'MongoDB', reason: 'The supplier catalog and chat history are documents that publish no events, which is what MongoDB suits.' },
      { name: 'Redis with FusionCache', reason: 'Sessions, presence, a 1-second micro-cache on every GET and the distributed locks behind request coalescing.' },
      { name: 'Debezium and Marten', reason: 'Debezium reads the Postgres WAL and MongoDB change streams, so a change made outside the app is flagged; Marten stores the Audit service\'s events in Postgres.' },
    ],
  },
  {
    title: 'Frontend',
    items: [
      { name: 'Angular 22 with signals and NgRx SignalStore', reason: 'Stale-while-revalidate caches in the stores, refreshed by live updates instead of polling.' },
      { name: 'Tailwind CSS and Angular Aria', reason: 'Accessible, unstyled building blocks with our own design, instead of a paid component library.' },
      { name: 'API clients generated from OpenAPI', reason: 'CI fails if the committed client differs from the services, so the frontend cannot drift from the APIs.' },
      { name: 'SignalR live updates and presence', reason: 'A request changes on screen when someone else acts on it, and you see who else is viewing it.' },
    ],
  },
  {
    title: 'AI',
    items: [
      { name: 'RAG built by hand on Microsoft.Extensions.AI', reason: 'Chunking, hybrid pgvector and full-text search, and citations are our own code, so every step is visible and tenant-filtered.' },
      { name: 'Tools that act as the signed-in user', reason: 'The assistant can never see more than the user could in the app.' },
      { name: 'Layered guardrails and invoice scanning', reason: 'Input and output checks around the model; invoice PDFs become structured lines that the user confirms.' },
      { name: 'Provider chosen by configuration', reason: 'Azure Foundry on the live demo and a deterministic fake locally and in CI, so tests never spend tokens.' },
    ],
  },
  {
    title: 'Delivery and operations',
    items: [
      { name: 'Helm and Argo CD (GitOps)', reason: 'The cluster is described in Git and synced by Argo CD (GitOps); database migrations run as sync hooks.' },
      { name: 'OpenTelemetry and the Aspire dashboard', reason: 'Logs, traces and metrics from every service, with one trace following a request across RabbitMQ.' },
      { name: '.NET Aspire and Testcontainers', reason: 'The whole system starts with one command locally, and integration tests run against real containers.' },
      { name: 'GitHub Actions and GHCR', reason: 'CI builds and checks client drift on every push and publishes the images.' },
    ],
  },
];

/** The text alternative for the architecture diagram: the picture's `alt`. */
export const DIAGRAM_ALT =
  'Architecture diagram: the Angular app talks to the gateway (YARP and BFF), which signs users in through Keycloak and forwards ' +
  'calls to the business services, the Assistant and Notifications. The services use Postgres and MongoDB and exchange events ' +
  'through RabbitMQ. Debezium reads the databases and publishes changes to RabbitMQ; the Audit service and Notifications consume them. ' +
  'Redis holds sessions and presence.';

/** The same flow in reading order, for screen readers (a visually hidden ordered list). */
export const DIAGRAM_STEPS: readonly string[] = [
  'The Angular app reaches the gateway (YARP with the BFF login) with an HttpOnly cookie and SignalR.',
  'The gateway signs users in with Keycloak (OIDC code flow with PKCE) and forwards calls with a JWT.',
  'The business services are Requests, Access, Budgets, Approvals, Purchasing, Invoicing and Suppliers; the gateway also reaches the Assistant (RAG, tools, invoice scan) and Notifications (SignalR).',
  'The Assistant calls the business services as the user and talks to the AI model provider through IChatClient and IEmbeddingGenerator.',
  'The services keep their data in Postgres with pgvector (one database each) and MongoDB, and exchange events over RabbitMQ with the Wolverine outbox and inbox; they also call the supplier and ERP simulators and receive signed webhooks.',
  'Debezium Server reads the Postgres WAL and the MongoDB change streams and publishes the changes to RabbitMQ.',
  'The Audit service (Marten) and Notifications consume the events from RabbitMQ.',
  'The gateway and Notifications keep sessions and presence in Redis.',
];

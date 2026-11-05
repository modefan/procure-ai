// Per-database least-privilege MongoDB users (plan 08 ruling 6), run by mongosh as the server's root user on EVERY start, before the
// migrations: under the AppHost by the one-shot container `mongo-users` (ServiceResources.AddMongoUsers), in Kubernetes by the db-roles
// Sync hook Job of the platform chart (templates/db-roles-job.yaml), in the tests by MongoFixture.RunUsersScriptAsync. Idempotent: a second
// run changes nothing, a changed password or database list is applied by the next run (updateUser replaces password and roles).
//
//   mongosh --quiet --norc <uri> users.js
//
// Users, all in the admin database (the connection strings say authSource=admin), each with exactly one role:
//   suppliers_app  readWrite on suppliersdb       (Suppliers; password DB_SUPPLIERS_PASSWORD)
//   assistant_app  readWrite on assistantchatdb   (the Assistant's chat history; password DB_ASSISTANT_MONGO_PASSWORD)
//   debezium       read on every captured database (DEBEZIUM_MONGO_DATABASES, comma separated) plus debeziumChangeStreams, the
//                  cluster-wide changeStream/find and listDatabases the one debezium-mongo connector needs for capture.scope=deployment
//                  (Debezium 3.7 MongoDB connector documentation, "Setting up MongoDB"); password DEBEZIUM_MONGO_PASSWORD. No write, no user
//                  administration, no system collections. Created by Suppliers' --migrate until plan 08 Task 4 (it ran as root then).
// The root user signs in from MONGO_USER / MONGO_PASSWORD. Nothing secret is printed.

function required(name) {
  const value = process.env[name];
  if (!value) {
    throw new Error(`users.js: environment variable ${name} is not set`);
  }
  return value;
}

const admin = db.getSiblingDB('admin');
admin.auth(required('MONGO_USER'), required('MONGO_PASSWORD'));

function ensureRole(name, privileges) {
  const exists = admin.runCommand({ rolesInfo: name }).roles.length > 0;
  const command = exists
    ? { updateRole: name, privileges: privileges, roles: [] }
    : { createRole: name, privileges: privileges, roles: [] };
  const result = admin.runCommand(command);
  if (result.ok !== 1) {
    throw new Error(`users.js: ${exists ? 'updateRole' : 'createRole'} ${name} failed: ${result.errmsg}`);
  }
  print(`role ${name}: ${exists ? 'updated' : 'created'}`);
}

function ensureUser(name, password, roles) {
  const exists = admin.runCommand({ usersInfo: { user: name, db: 'admin' } }).users.length > 0;
  const command = exists
    ? { updateUser: name, pwd: password, roles: roles }
    : { createUser: name, pwd: password, roles: roles };
  const result = admin.runCommand(command);
  if (result.ok !== 1) {
    throw new Error(`users.js: ${exists ? 'updateUser' : 'createUser'} ${name} failed: ${result.errmsg}`);
  }
  print(`user ${name}: ${exists ? 'updated' : 'created'} (${roles.map(r => `${r.role}@${r.db}`).join(', ')})`);
}

ensureUser('suppliers_app', required('DB_SUPPLIERS_PASSWORD'), [{ role: 'readWrite', db: 'suppliersdb' }]);
ensureUser('assistant_app', required('DB_ASSISTANT_MONGO_PASSWORD'), [{ role: 'readWrite', db: 'assistantchatdb' }]);

const captured = required('DEBEZIUM_MONGO_DATABASES').split(',').map(d => d.trim()).filter(d => d.length > 0);
if (captured.length === 0) {
  throw new Error('users.js: DEBEZIUM_MONGO_DATABASES names no database');
}
ensureRole('debeziumChangeStreams', [
  { resource: { db: '', collection: '' }, actions: ['changeStream', 'find'] },
  { resource: { cluster: true }, actions: ['listDatabases'] },
]);
ensureUser('debezium', required('DEBEZIUM_MONGO_PASSWORD'), [
  ...[...new Set(captured)].map(d => ({ role: 'read', db: d })),
  { role: 'debeziumChangeStreams', db: 'admin' },
]);
print('users.js: done');

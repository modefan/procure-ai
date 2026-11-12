# Sets the pgAdmin user's authenticator (TOTP) secret from the mounted Secret (plan 08b Task 4), so the one-time code can be computed from
# DEMO PASSWORDS.txt and survives a restart of the pod (pgAdmin's database is an emptyDir, rebuilt at every start). Started in the
# background next to the image's entrypoint; waits until the entrypoint has created the user, writes the secret into user_mfa (what pgAdmin's
# own registration writes: mfa_auth 'authenticator', options = the Base32 secret), then leaves a marker the startup probe waits for, so the
# pod takes no traffic before the code is required. Prints nothing secret.
import os
import sqlite3
import sys
import time

DATABASE = '/var/lib/pgadmin/pgadmin4.db'
MARKER = '/var/lib/pgadmin/.mfa-ready'
email = os.environ['PGADMIN_DEFAULT_EMAIL']
with open('/etc/pgadmin-secrets/totp-base32', encoding='ascii') as f:
    secret = f.read().strip()
if not secret:
    sys.exit('seed-mfa: the TOTP secret is empty')

for _ in range(600):
    try:
        connection = sqlite3.connect(DATABASE, timeout=5)
        try:
            tables = {r[0] for r in connection.execute("SELECT name FROM sqlite_master WHERE type = 'table'")}
            user = None
            if {'user', 'user_mfa'} <= tables:
                user = connection.execute('SELECT id FROM "user" WHERE lower(email) = lower(?)', (email,)).fetchone()
            if user:
                connection.execute('DELETE FROM user_mfa WHERE user_id = ?', (user[0],))
                connection.execute(
                    'INSERT INTO user_mfa (user_id, mfa_auth, options) VALUES (?, ?, ?)', (user[0], 'authenticator', secret))
                connection.commit()
                open(MARKER, 'w').close()
                print('seed-mfa: authenticator set for the pgAdmin user', flush=True)
                sys.exit(0)
        finally:
            connection.close()
    except sqlite3.Error:
        pass
    time.sleep(1)
sys.exit('seed-mfa: the pgAdmin user did not appear within 10 minutes')

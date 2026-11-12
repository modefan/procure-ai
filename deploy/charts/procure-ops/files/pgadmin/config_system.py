# pgAdmin's system configuration (read from /etc/pgadmin/config_system.py after config.py and config_distro.py), plan 08b Task 4.
# One administrator user (from PGADMIN_DEFAULT_EMAIL / PGADMIN_DEFAULT_PASSWORD_FILE) with a pre-set authenticator (seed-mfa.py), no
# other sign-in source, no self-registration (pgAdmin has none with internal authentication), nothing that calls out.
SERVER_MODE = True
AUTHENTICATION_SOURCES = ['internal']
DATA_DIR = '/var/lib/pgadmin'

# Two-factor sign-in: an authenticator app only (no e-mail codes: no mail server). The secret is set at every start (seed-mfa.py).
MFA_ENABLED = True
MFA_SUPPORTED_METHODS = ['authenticator']
MFA_FORCE_REGISTRATION = True

# No account lockout: pgAdmin locks an account for good until an administrator unlocks it, and the one administrator is Dale, so a stranger
# typing wrong passwords would lock him out. Password guessing is slowed by Traefik's rate limit (values.yaml rateLimit) instead.
MAX_LOGIN_ATTEMPTS = 0

# Session cookie: https only, not readable by scripts; tied to the client (ENHANCED_COOKIE_PROTECTION). TLS ends at Traefik, which sets one
# X-Forwarded-For / -Proto hop (the defaults PROXY_X_FOR_COUNT / PROXY_X_PROTO_COUNT = 1).
SESSION_COOKIE_SECURE = True
SESSION_COOKIE_HTTPONLY = True
SESSION_COOKIE_SAMESITE = 'Lax'
ENHANCED_COOKIE_PROTECTION = True
MAX_SESSION_IDLE_TIME = 60
SESSION_EXPIRATION_TIME = 1

# Database passwords are never stored in pgAdmin: the read-only server takes its password from the mounted Secret (SERVER_PASSEXEC_COMMANDS,
# the only command a server may name), the superuser's is typed in an emergency and forgotten.
ALLOW_SAVE_PASSWORD = False
ALLOW_SAVE_TUNNEL_PASSWORD = False
MASTER_PASSWORD_REQUIRED = False
SERVER_PASSEXEC_COMMANDS = {
    'operator-reader': ['/bin/cat', '/etc/pgadmin-secrets/reader-password'],
}

# Nothing that reaches out or runs local programs (egress is closed by the namespace's NetworkPolicies anyway).
UPGRADE_CHECK_ENABLED = False
LLM_ENABLED = False
AUTO_DISCOVER_SERVERS = False
ENABLE_PSQL = False
SHARED_STORAGE = []

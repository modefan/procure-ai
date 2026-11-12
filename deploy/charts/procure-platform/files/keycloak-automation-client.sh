# The automation's own admin access to Keycloak (plan 08b Task 4 fix round 1, reviewer's I3/R3 option), run by the keycloak-config Job's
# init container `automation-client` (our Keycloak image: kcadm.sh) on every sync, before keycloak-config-cli and the operator-user
# container sign in with it. Environment: CLIENT_ID, CLIENT_SECRET (Secret procure-keycloak), ADMIN_USER, ADMIN_PASSWORD (the bootstrap
# admin, only for the first run), DISABLE_BOOTSTRAP_ADMIN (true|false), HOME (a memory volume for kcadm's config and the request file).
# Nothing secret reaches argv or the log.
#   1. sign in to the master realm as the confidential client CLIENT_ID (client credentials: no user, so no user lockout); when that fails
#      (a fresh install, or the first sync after this change) as the bootstrap admin with its password, the only time that is used
#   2. keep the client as Git describes it: confidential, service account only (no browser or password flows), the master realm's role
#      admin, the secret from the Secret (create it, or update it when signed in as the bootstrap admin)
#   3. prove the client signs in, and only then disable the bootstrap admin (DISABLE_BOOTSTRAP_ADMIN), so the Job can never lock itself out
#      by switching off the account it still needs. The bootstrap admin stays as break-glass (procure-ai docs/hosting.md, "Keycloak's
#      automation client and the bootstrap admin"): re-enable it with kcadm as this client through the SSH tunnel.
set -eu
umask 077
kc=/opt/keycloak/bin/kcadm.sh
cfg="$HOME/kcadm.config"
server=http://keycloak:8080
as_client() {
  KC_CLI_CLIENT_SECRET="$CLIENT_SECRET" "$kc" config credentials --config "$cfg" --server "$server" --realm master --client "$CLIENT_ID" >/dev/null 2>&1
}
as_admin() {
  KC_CLI_PASSWORD="$ADMIN_PASSWORD" "$kc" config credentials --config "$cfg" --server "$server" --realm master --user "$ADMIN_USER" >/dev/null 2>&1
}

signed_in=''
for attempt in $(seq 1 60); do
  if as_client; then signed_in=client; break; fi
  if as_admin; then signed_in=admin; break; fi
  if [ "$attempt" -eq 60 ]; then
    echo "automation client: neither $CLIENT_ID (client credentials) nor the bootstrap admin can sign in to the master realm" >&2
    echo 'see procure-ai docs/hosting.md "Keycloak'"'"'s automation client and the bootstrap admin" (break-glass)' >&2
    exit 1
  fi
  sleep 5
done
echo "automation client: signed in as $([ "$signed_in" = client ] && echo "$CLIENT_ID" || echo 'the bootstrap admin (first run)')"

# A JSON string's content: \ and " escaped.
js() { printf '%s' "$1" | sed 's/[\\"]/\\&/g'; }
id=$("$kc" get clients --config "$cfg" -r master -q "clientId=$CLIENT_ID" --fields id,clientId --format csv --noquotes \
  | while IFS=, read -r cid name; do [ "$name" = "$CLIENT_ID" ] && echo "$cid"; done || true)
cat > "$HOME/client.json" <<JSON
{"clientId":"$(js "$CLIENT_ID")","name":"procure automation","description":"keycloak-config Job and kcadm (client credentials); procure-ai docs/hosting.md",
 "enabled":true,"protocol":"openid-connect","publicClient":false,"bearerOnly":false,"clientAuthenticatorType":"client-secret",
 "secret":"$(js "$CLIENT_SECRET")","serviceAccountsEnabled":true,"standardFlowEnabled":false,"implicitFlowEnabled":false,
 "directAccessGrantsEnabled":false,"frontchannelLogout":false,"consentRequired":false,
 "attributes":{"oauth2.device.authorization.grant.enabled":"false","oidc.ciba.grant.enabled":"false"}}
JSON
if [ -z "$id" ]; then
  "$kc" create clients --config "$cfg" -r master -f "$HOME/client.json" >/dev/null
  echo "automation client $CLIENT_ID: created"
else
  "$kc" update "clients/$id" --config "$cfg" -r master -f "$HOME/client.json"
  echo "automation client $CLIENT_ID: settings and secret applied"
fi
rm -f "$HOME/client.json"
"$kc" add-roles --config "$cfg" -r master --uusername "service-account-$CLIENT_ID" --rolename admin
echo "automation client $CLIENT_ID: realm role admin"

# Proof before the bootstrap admin goes: a fresh sign-in as the client (a few tries: the new secret and role take effect at once, but be
# patient with a busy server).
for attempt in $(seq 1 10); do
  if as_client; then break; fi
  if [ "$attempt" -eq 10 ]; then echo "automation client $CLIENT_ID: cannot sign in with its secret; the bootstrap admin is left as it is" >&2; exit 1; fi
  sleep 3
done
echo "automation client $CLIENT_ID: signs in"

if [ "${DISABLE_BOOTSTRAP_ADMIN:-false}" = true ]; then
  uid=$("$kc" get users --config "$cfg" -r master -q exact=true -q "username=$ADMIN_USER" --fields id --format csv --noquotes | head -n 1)
  if [ -n "$uid" ]; then
    "$kc" update "users/$uid" --config "$cfg" -r master -s enabled=false
    echo "bootstrap admin $ADMIN_USER: disabled (break-glass only)"
  else
    echo "bootstrap admin $ADMIN_USER: not found (nothing to disable)"
  fi
fi

# Dale's own admin in Keycloak's master realm (plan 08b Task 4), run by the keycloak-config Job's operator-user container (our Keycloak
# image: kcadm.sh) on every sync through the Service, as the automation client (CLIENT_ID, CLIENT_SECRET) when set, else as the bootstrap
# admin (ADMIN_USER, ADMIN_PASSWORD). Environment: OPERATOR_USERNAME, OPERATOR_PASSWORD and OPERATOR_TOTP_BASE32 (Secret procure-keycloak; the last two optional: without them nothing happens), HOME (a
# memory volume for kcadm's config and the request files). Nothing secret reaches argv or the log.
#   missing user                     created with the password and an OTP credential holding the TOTP secret, labelled
#                                    "procure <first 16 hex digits of the SHA-256 of the Base32 secret>"
#   no OTP credential with that label deleted and created again (Keycloak has no API that replaces an OTP credential)
# The fingerprint is in the credential's label, not a user attribute: Keycloak 26's user profile drops attributes it does not declare
# (unmanaged attributes are off), so an attribute never came back and the user was recreated on every sync (plan 08b Task 4 ruling R2).
#   otherwise                        the password is set again (a rotated password reaches Keycloak with the next sync)
#   always                           the master realm's role admin
set -eu
if [ -z "${OPERATOR_PASSWORD:-}" ] || [ -z "${OPERATOR_TOTP_BASE32:-}" ]; then
  echo 'operator user: no operator-password or operator-totp-base32 in Secret procure-keycloak; skipped'
  exit 0
fi
umask 077
kc=/opt/keycloak/bin/kcadm.sh
cfg="$HOME/kcadm.config"
sign_in() {
  # The automation client when CLIENT_ID is set (keycloakConfig.automationClient, plan 08b Task 4 fix round 1: client credentials, no
  # password), else the bootstrap admin.
  if [ -n "${CLIENT_ID:-}" ]; then
    KC_CLI_CLIENT_SECRET="$CLIENT_SECRET" "$kc" config credentials --config "$cfg" --server http://keycloak:8080 --realm master \
      --client "$CLIENT_ID" >/dev/null 2>&1
  else
    KC_CLI_PASSWORD="$ADMIN_PASSWORD" "$kc" config credentials --config "$cfg" --server http://keycloak:8080 --realm master \
      --user "$ADMIN_USER" >/dev/null 2>&1
  fi
}
for attempt in $(seq 1 60); do
  if sign_in; then break; fi
  if [ "$attempt" -eq 60 ]; then echo "operator user: could not sign in to Keycloak as ${CLIENT_ID:-the bootstrap admin}" >&2; exit 1; fi
  sleep 5
done

# A JSON string's content: \ and " escaped.
js() { printf '%s' "$1" | sed 's/[\\"]/\\&/g'; }
# Keycloak keeps the TOTP key as text and shows the authenticator app its Base32 form, so the Base32 secret is decoded to that text.
raw=$(printf '%s' "$OPERATOR_TOTP_BASE32" | base32 -d)
label="procure $(printf '%s' "$OPERATOR_TOTP_BASE32" | sha256sum | cut -c1-16)"
user="$OPERATOR_USERNAME"

id=$("$kc" get users --config "$cfg" -r master -q exact=true -q "username=$user" --fields id --format csv --noquotes | head -n 1)
if [ -n "$id" ]; then
  # One line per credential: "<type>,<userLabel>".
  creds=$("$kc" get "users/$id/credentials" --config "$cfg" -r master --fields type,userLabel --format csv --noquotes)
  if ! printf '%s\n' "$creds" | grep -qxF "otp,$label"; then
    "$kc" delete "users/$id" --config "$cfg" -r master
    echo "operator user $user: authenticator secret new or changed; user recreated"
    id=''
  fi
fi

if [ -z "$id" ]; then
  # secretData and credentialData are JSON strings inside the JSON, hence the second escaping of the key.
  cat > "$HOME/user.json" <<JSON
{"username":"$(js "$user")","enabled":true,"firstName":"Operator","lastName":"Dale","emailVerified":true,
 "credentials":[{"type":"password","value":"$(js "$OPERATOR_PASSWORD")","temporary":false},
  {"type":"otp","userLabel":"$label","secretData":"{\\"value\\":\\"$(js "$(js "$raw")")\\"}",
   "credentialData":"{\\"subType\\":\\"totp\\",\\"digits\\":6,\\"counter\\":0,\\"period\\":30,\\"algorithm\\":\\"HmacSHA1\\"}"}]}
JSON
  "$kc" create users --config "$cfg" -r master -f "$HOME/user.json" >/dev/null
  rm -f "$HOME/user.json"
  echo "operator user $user: created with password and authenticator"
else
  printf '{"type":"password","value":"%s","temporary":false}' "$(js "$OPERATOR_PASSWORD")" > "$HOME/password.json"
  "$kc" update "users/$id/reset-password" --config "$cfg" -r master -f "$HOME/password.json"
  rm -f "$HOME/password.json"
  echo "operator user $user: password set"
fi
"$kc" add-roles --config "$cfg" -r master --uusername "$user" --rolename admin
echo "operator user $user: realm role admin"

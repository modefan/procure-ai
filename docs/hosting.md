# Hosting the live demo

The live demo runs on one virtual machine, a Contabo Cloud VPS with k3s. Argo CD deploys it from
[procure-ai-gitops](https://github.com/modefan/procure-ai-gitops), with the same GitOps flow as the local cluster
([kubernetes.md](kubernetes.md)). The whole hosted setup is code. One Terraform root in `procure-ai-gitops/infra/hosted/` builds the VM:
it adopts the paid instance and reinstalls it with Ubuntu, the SSH key and cloud-init, which installs k3s. The same root also sets up the
VM's firewall and the Cloudflare DNS records. The bootstrap scripts in `procure-ai-gitops/bootstrap/hosted/` run from Dale's PC through an
SSH tunnel.

## What runs where

| Piece | Choice | Why |
|---|---|---|
| VM | **Contabo Cloud VPS 8**: 8 vCPU x86, 24 GB, 300 GB SSD, region EU (data center "European Union 2"), Ubuntu 26.04 LTS. Instance `203735351`, IPv4 `45.13.235.178`, IPv6 `2a02:c207:2373:5351::1` | The full stack needs about 9 GiB plus Argo CD. x86 runs the published images as they are. Ubuntu 24.04 is the fallback if k3s ever misbehaves on 26.04 (`image_id` in `infra/hosted/variables.tf`). |
| Kubernetes | **k3s** v1.35.10+k3s1 (single node, the local cluster's minor), with Traefik as shipped (on host ports 80/443, not behind ServiceLB; Task 12), flannel **host-gw** and IPv4 only | Same distribution as the local k3d cluster. host-gw needs no tunnel on one node and opens no socket; VXLAN would listen on UDP 8472, an unauthenticated way into the pod network. |
| Network | Two firewalls. Contabo's firewall allows inbound **only TCP 22, 80, 443** (IPv4 and IPv6), then Contabo's own "block all traffic" rule. On the VM, the nftables table `inet procure_host` (cloud-init, service `procure-host-firewall`) lets in only replies, 22, 80, 443 and the ICMPv6 that IPv6 needs on `eth0`, and forwards only 80/443 to pods (never a NodePort). No ping: the VM does not answer it, which does not mean it is down. | The Kubernetes API, the kubelet and kube-proxy's health port listen only on the node address `10.0.1.2`. That address is on a dummy interface, because Contabo's private networks are a paid add-on. |
| GitOps | Argo CD on the VM, root Application on `envs/hosted/` (platform, observability, procure-ai). The publish workflow bumps `envs/hosted/release.yaml` together with `envs/local/release.yaml`, so the public demo follows main | Same app of apps and charts as local; only the values in `envs/hosted/` differ. |
| Edge | Traefik as k3s ships it, configured by a HelmChartConfig: http answers with a permanent redirect to https, every https answer carries HSTS, and Traefik takes 80/443 itself as host ports so it sees the visitor's address (its Service is a ClusterIP). One Let's Encrypt certificate (cert-manager, HTTP-01) for `procure`, `auth` and `dashboard.daleabels.com` is Traefik's default certificate | The charts stay as they are: every Ingress without its own secret gets the default certificate, as the local CA's does locally. One certificate is one ACME order and one renewal. |
| Models | Azure Foundry (Task 9): deployments `chat` (gpt-5-mini) and `embeddings` (text-embedding-3-small, 1536 dimensions), key in Secret `procure-ai-provider` | Configuration only (`envs/hosted/procure-ai.yaml`); local and CI keep the Fake provider. |
| DNS | Cloudflare (`daleabels.com`): an **A** record each for `procure`, `auth` and `dashboard`, DNS only (not proxied), TTL 300 s. **No AAAA**: k3s is IPv4 only (Traefik's host ports are IPv4 only), so IPv6 visitors and Let's Encrypt would reach an address where 80/443 do not answer. IPv6-only visitors cannot reach the demo. Task 12 decided to stay IPv4-only (below). | Kept by the same Terraform root (official `cloudflare/cloudflare` provider, account-owned token with DNS Write on this zone only). The apex and other records are not touched. |

**Why Contabo and not Hetzner.** Plan 08 chose Hetzner (cx43, EUR 15.99 a month). But every affordable 16 GB type (cx43, cx53, cax31,
cpx41) was sold out in all EU locations for the whole day of 2026-11-07. The next types in stock cost EUR 69 or more. The Terraform for
Hetzner is in git history (`infra/hetzner`, before the move to `infra/hosted`).

## Security baseline (checked by `bootstrap/hosted/Test-HostBaseline.ps1`)

- **SSH.** There is one non-root sudo user, `procure` (Contabo's merged config also creates the image's `ubuntu` user; cloud-init
  deletes it), who logs in with an ed25519 key only. The private half of the key is only on Dale's
  PC. There is no root login and no password or keyboard-interactive login, and only that user may log in. Agent and X11 forwarding are
  off; only local port forwarding is allowed (the API tunnel). fail2ban (journal of `ssh.service`, including `sshd-session`) bans
  addresses that keep failing.
- **Contabo's own cloud-config.** On every boot it sets `PermitRootLogin yes` in `sshd_config` and sets the root password. Our
  `sshd_config.d/00-procure-hardening.conf` is read first and wins. The root password works only on Contabo's VNC console, which the
  Contabo API cannot switch off; Dale can switch it off in the panel. Its `pkill -HUP sshd` fails on Ubuntu 26.04, so `cloud-init status`
  says "error" on every boot. The bootstrap accepts that error only when the merged bootcmd list on the VM is exactly Contabo's three
  commands (our cloud-init has no bootcmd).
- **Where Contabo keeps the root password.** On every reinstall Contabo writes the root password Dale received in plain text into the
  VM's cloud-init config disk (`/dev/sr0`, readable by root and the `cdrom` group, which no user is in) and into
  `/var/lib/cloud/instance/cloud-config.txt` (root only, 0600). The value comes from the Contabo password secret
  `procure-hosted-root-password`, which keeps Dale's password valid for the console. Anyone with root on the VM can read it, so it must
  never be reused elsewhere.
- **Updates.** Unattended security upgrades are on. The VM does **not** reboot by itself: a reboot restarts the whole demo, so it is a
  deliberate step. Test-HostBaseline warns when an update waits for a reboot. Then reboot at a quiet time (`ssh ... sudo systemctl
  reboot`). The kubelet gives pods 60 s to stop (checked: the pods ended "terminated in response to imminent node shutdown"), and k3s
  comes back on its own within about 30 s.
- **The Kubernetes API (6443)** is not reachable from the internet: it listens on the node address only, and both firewalls drop it
  anyway. Test-HostBaseline checks every TCP and UDP listener on the VM against an allowlist. kubectl and helm reach it through `bootstrap/hosted/open-tunnel.ps1` (ssh `-L 127.0.0.1:16443:10.0.1.2:6443`).
- **Graceful shutdown.** The kubelet shuts pods down gracefully when the VM shuts down: `shutdownGracePeriod` 60 s, 20 s of it for
  critical pods. logind waits 75 s. Its drop-in must sort after unattended-upgrades' own 30 s file, or the kubelet's shutdown manager does
  not start.
- **AppArmor and k3s's runc (Task 12).** Ubuntu 26.04 keeps `kernel.apparmor_restrict_unprivileged_unconfined` and
  `..._userns` at 1, which turns an unprivileged, unconfined process's profile change into a stack. k3s's bundled runc has no profile,
  so every non-root container ran as `cri-containerd.apparmor.d//&unconfined` and could not signal its own processes: Postgres and
  MongoDB failed their first-start init ("pg_ctl: could not send stop signal: Permission denied") and left half-initialised volumes.
  Ubuntu gives its own `/usr/sbin/runc` the profile `runc` (unconfined flag, `userns`); `/etc/apparmor.d/procure-k3s-runc` (cloud-init)
  is that profile with k3s's runc path added, and Ubuntu's file is disabled (`/etc/apparmor.d/disable/runc`) so one definition loads.
  Both kernel restrictions stay on, containers keep containerd's profile, and containerd's profile accepts signals from `runc`, so
  stopping containers works. Test-HostBaseline checks the sysctls, the profile, that no process carries a stacked label and that the
  kernel log has no container signal denials. Lifting the sysctl instead was rejected in review (a host-wide security control).
  The profile check is a probe of the attachment the kernel really uses: a copy of `grep` at a path of k3s's runc pattern
  (`data/<dir>/bin/runc`) must print the label `runc (unconfined)`. If a k3s release ever ships runc outside `data/<hash>/bin`, or
  Ubuntu renames its `runc` profile, that check (and the stacked-label check) is the alarm.
- **Memory reservations** (24 GB VM): kube 1.5 GiB, system 512 MiB, eviction below 1 GiB free. Traefik, the public edge, has a memory
  limit of 256 MiB (31 MiB at idle, measured 2026-11-08), so a connection flood restarts Traefik instead of eating the node's headroom.
- **No ServiceLB.** k3s runs with `disable: [servicelb]` (`/etc/rancher/k3s/config.yaml`): Traefik takes 80/443 as host ports, and a
  future LoadBalancer Service would otherwise get svclb host ports on every interface. It stays Pending instead.
- **Password guessing on Keycloak** (plan 08 fix wave ruling R6b). Slowed **per client address** at Traefik, not by locking users:
  Keycloak's per-user lockout is off (`bruteForceProtected: false` in the realm file, explicitly, so keycloak-config-cli turns it off on a
  running realm too). The demo users are shared and their names public, so a lockout let any stranger keep every "log in as" button
  failing with a few wrong passwords a minute (the review of R6; the lockout was live for half an hour on 2026-11-08).
  - Traefik RateLimit middlewares (procure-platform `keycloak.ingress.rateLimit`, on in `envs/hosted/platform.yaml`; ruling R6c).
  - **Every path** of `auth.daleabels.com`: 120 a minute, bursts of 60, per address **and per path prefix** (Traefik keeps one
    bucket per router: the procure realm, the ops realm and `/resources/` each have their own). The keycloak Ingress itself carries the
    middleware, so no spelling of a sign-in path (`;x=1` matrix parameters, encoded or upper-case segments) reaches Keycloak without the
    limit. The token precedence relies on Traefik's default path cleaning; never set `sanitizePath=false` on an entry point.
  - The token endpoints (`/realms/<realm>/protocol/openid-connect/token`): their own Ingress and a higher limit, 600 a minute, bursts of
    120. Inside the cluster the gateway reaches them through Traefik too (CoreDNS rewrite), so the gateway pod is one address for every
    visitor's code exchange, refresh and demo login; that limit is sized for all of them together. Another spelling of the token path
    falls back to the stricter limit above.
  - The gateway's back-channel logout (`end_session` at `/realms/procure/protocol/openid-connect/logout`) and its discovery and key
    fetches share the gateway pod's `/realms/procure/` bucket (120 a minute). In normal use that is far from full; a scripted demo-login
    and logout loop from four or more addresses could fill it. Keycloak's session then stays open until it expires (the gateway logs a
    warning and removes its own session anyway), so nothing is exposed (re-review m2, documented rather than exempting the pod CIDR).
  - The demo realm's built-in `admin-cli` client takes no password grants (`directAccessGrantsEnabled: false` in the realm file;
    re-review m3), so the token endpoint offers no public client for password guessing in a realm without lockout.
  - Over a limit Traefik answers 429. Traefik sees the visitor's own address (host ports) and trusts no `X-Forwarded-For`, so a header
    cannot dodge the limit. IPv6 grouping does not apply: the host has A records only.
  - `tests/Test-PublicSurface.ps1` step 9b fires bursts at a matrix-parameter variant of the sign-in path and at the realm's discovery
    document, and needs 429s from both.
  - Proven on 2026-11-08 that the lockout is off: a throwaway user took 31 wrong passwords and then signed in with the right one. The
    earlier login-path-only middleware, applied for a minute as a copy, let 32 of 80 parallel requests through and answered 429 to 48.
    The every-path version is proven after the push (Test-PublicSurface 9b).
  - Realm `ops` (Argo CD's sign-in) is different: personal users, so it has a lockout and OTP (below).
- **Secrets.** Kubernetes Secrets are encrypted at rest (k3s `secrets-encryption`).
- **Nothing secret in a repo.** No Terraform state, kubeconfig, SSH key or Secret is in any repo. They live in
  `%USERPROFILE%\.procure-ai\hosted\` on Dale's PC (current user only) and are listed by name in DEMO PASSWORDS.txt. The hosted Secrets
  come from their own user-secrets store `procure-ai-hosted` (`new-secrets.ps1 -Environment hosted`), so the public demo shares no secret
  with the local setup. The registry pull Secret uses a read-only GHCR token, not the GitHub CLI token. **That token expires on
  2027-02-05** (`GhcrPullTokenExpires` in `bootstrap/common.ps1`; Test-HostBaseline warns 21 days before and fails after). While the
  packages are private, an expired token makes the next deploy fail with ImagePullBackOff (running pods keep their cached images). Renew:
  a new classic token with only `read:packages`, into DEMO PASSWORDS.txt and the user env var `GHCR_READ_TOKEN`, then
  `new-secrets.ps1 -Environment hosted` and the new date in `common.ps1`. Not needed once the packages are public.
- **Every generated secret is also in DEMO PASSWORDS.txt.** `new-secrets.ps1` copies every value of the user-secrets store it reads
  into DEMO PASSWORDS.txt on every run (section "procure-ai hosted (Contabo) secrets", or "procure-ai local: all AppHost user secrets"),
  so a generated or rotated value is never only in the store. The file is rewritten only when a value changed, through a temporary file,
  and no `.bak` copy stays on the Desktop.

## Bringing it up (from Dale's PC)

Needs `terraform`, `kubectl`, `helm`, Windows OpenSSH, `gh` and the .NET SDK, plus these user env vars: `CONTABO_CLIENT_ID`,
`CONTABO_CLIENT_SECRET`, `CONTABO_USER` and `CONTABO_PASSWORD` (Contabo API), `CLOUDFLARE_API_TOKEN` (DNS token for daleabels.com) and
`GHCR_READ_TOKEN`.

```powershell
cd procure-ai-gitops
./bootstrap/hosted/new-hosted-cluster.ps1 -PlanOnly   # plan + money guard: SSH key secret, the adopted VPS, firewall, 3 DNS A records
./bootstrap/hosted/new-hosted-cluster.ps1 -Reinstall  # first time only: adopts and REINSTALLS the VPS, cloud-init, kubeconfig, tunnel, namespaces
./bootstrap/hosted/new-hosted-cluster.ps1             # any later time: applies firewall/DNS changes, re-checks SSH, cloud-init, the cluster
./bootstrap/new-secrets.ps1 -Environment hosted       # Secrets from the procure-ai-hosted user secrets (generated on the first run)
./bootstrap/install-argocd.ps1 -Environment hosted    # Argo CD (Keycloak sign-in, admin as break-glass, no ingress), root Application
./bootstrap/new-argocd-token.ps1 -Environment hosted  # Claude's API token (account claude) -> user env var ARGOCD_AUTH_TOKEN_HOSTED
./bootstrap/hosted/Test-HostBaseline.ps1              # the checks above, from the PC
./bootstrap/hosted/open-tunnel.ps1 -Close             # close the tunnel when done
```

Every script is safe to run again. **Run Terraform in `infra/hosted` only through `new-hosted-cluster.ps1`**: its plan check guards
every apply and destroy, and a raw `terraform apply` skips it. Never delete the `contabo_instance` block from `main.tf`: once it is gone,
`prevent_destroy` no longer protects it and a raw apply would cancel the VPS. `new-hosted-cluster.ps1` refuses a plan that would:

- order an instance (a create without `existing_instance_id = 203735351`);
- delete the instance (for the Contabo provider, delete means *cancel*; `prevent_destroy` blocks it as well);
- add any other paid Contabo resource or add-on, or DNS records other than the three A records (deleting our own records is allowed);
- remove the firewall of the running VM;
- reinstall the VM without `-Reinstall`.

Changes to `cloud-init.yaml` do not touch a running VM, because Terraform ignores them. Make the change on the VM over SSH too
(`ssh -i %USERPROFILE%\.procure-ai\hosted\procure-hosted_ed25519 procure@45.13.235.178`, with the `known_hosts` file there), or reinstall
on purpose with `-Reinstall`.

After a reinstall, check once by hand the things the baseline cannot see from outside:

- `kubectl logs` and `kubectl exec` into a kube-system pod, and `kubectl top node`, through the tunnel;
- a failed SSH login raises `Total failed` in `sudo fail2ban-client status sshd`;
- `sudo k3s check-config` passes;
- one deliberate reboot brings k3s back;
- a second `-PlanOnly` shows no changes.

The Argo CD UI has no public address: `./bootstrap/hosted/open-argocd.ps1` opens it (see "Argo CD on the VM" below).

### Changing the hosted values

Argo CD renders `envs/hosted/` on the public VM; a values file that does not render (a typo, a renamed key) shows up there first as a
ComparisonError or a failed sync. Two checks render it earlier (plan 08 final review B I1):

- **procure-ai CI** (charts job, every push): every chart with `envs/local/` and `envs/hosted/` values through `kubeconform -strict`,
  and the chart parity tests (`ChartParityTests`) against both environments' `procure-ai.yaml`. A chart change that needs a new hosted
  value fails CI, so push the GitOps values first (GitOps pushes cost no Actions minutes).
- **procure-ai-gitops has no CI**, so before every push there that changes `envs/`, `apps/` or `argocd/`:

  ```powershell
  cd procure-ai-gitops
  ./tests/Test-RenderEnvs.ps1                       # both environments, charts at each env's appRevision, Argo CD's chart
  ./tests/Test-RenderEnvs.ps1 -Environment hosted -AppRef WORKTREE -AppRepo ..\procure-ai.worktrees\<branch>   # an unpushed chart change
  ```

  It renders as Argo CD's repo-server does (dependencies as committed; `helm dependency build` only for one missing from `charts/`).

**Chart downloads.** Argo CD's repo-server renders without GitHub Pages now (cold renders timed out on its 504s): the collector chart
`opentelemetry-collector-0.178.2.tgz` is committed in `deploy/charts/procure-observability/charts/` (CI re-downloads it and fails if the
bytes differ), and Argo CD's own chart comes from its OCI copy `ghcr.io/argoproj/argo-helm` (`apps/values.yaml`; `install-argocd.ps1`
installs from the same registry). After the next push that touches either, check on the VM with a hard refresh of `observability` and
`argocd` in Argo CD (or `kubectl -n argocd annotate application observability argocd.argoproj.io/refresh=hard --overwrite`): both must
stay Synced without a ComparisonError.

## The public environment (plan 08 Task 12)

After the VM is up (above), four more steps make it the public demo. All are safe to run again:

```powershell
cd procure-ai-gitops
./bootstrap/hosted/install-cert-manager.ps1                     # first time: staging (the default), proves the ACME flow
./bootstrap/hosted/install-cert-manager.ps1 -Production         # then the real certificate; every later run needs -Production too
./bootstrap/new-local-ca.ps1 -Environment hosted                 # the VM's own internal CA for MongoDB's TLS (no browser sees it)
./bootstrap/new-secrets.ps1 -Environment hosted                  # incl. procure-ai-provider (Azure Foundry key from AZURE_FOUNDRY_API_KEY)
# Argo CD (install-argocd.ps1 -Environment hosted, above) then brings up envs/hosted on its own.
./tests/Test-PublicSurface.ps1                                   # what the internet sees (below)
./tests/Test-Smoke.ps1 -Environment hosted
```

`install-cert-manager.ps1` does the edge:

- **Traefik** (HelmChartConfig `kube-system/traefik`): `http://` answers 301 to `https://` with path and query kept, for every host.
  Let's Encrypt's HTTP-01 check follows that redirect to port 443, where cert-manager's solver Ingress answers. Every https answer
  carries `Strict-Transport-Security: max-age=31536000` (a Headers middleware on the websecure entry point; no `includeSubDomains`, no
  preload, nothing hard to undo).
- **The visitor's address.** k3s's ServiceLB cannot keep it: its svclb pod DNATs to the node and masquerades, so Traefik saw every
  visitor as that pod's address, also with `externalTrafficPolicy: Local` (measured in Task 12's fix round; all visitors shared one
  rate-limit bucket). So Traefik binds 80 and 443 itself as **host ports** (the CNI portmap plugin DNATs and keeps the source address)
  and its Service is a plain ClusterIP: no ServiceLB pod, no NodePorts. The host firewall is unchanged (it forwards only connections to
  80/443). A Traefik rollout replaces the pod (maxSurge 0: the new pod cannot bind the ports while the old one holds them), a few
  seconds without ingress, only when this config or k3s's Traefik changes. Ingresses get `procure.daleabels.com` as their status
  address. Traefik trusts no incoming `X-Forwarded-*` (nothing sits in front of the VM), puts the visitor's address into
  `X-Forwarded-For`, and the gateway trusts that header from the pod network only, where only Traefik may connect (plan 08 ruling 5).
  The gateway's rate limits see the real client address; `Test-PublicSurface.ps1` proves it with an echo pod.
- **CoreDNS:** inside the cluster `auth.daleabels.com` resolves to Traefik, so pods fetch Keycloak's discovery and keys through the same
  host name and certificate as the browser (the hosted twin of the local rewrite).
- **HSTS and a rebuilt VM.** A browser that opened the demo before keeps the one-year HSTS entry, so while a rebuilt VM serves the
  staging certificate (the first `install-cert-manager.ps1` run) or Traefik's self-signed default before the first certificate, that
  browser shows a certificate error it does not let you click through. That is expected for those minutes: test with `curl -k` or
  `openssl s_client` until `install-cert-manager.ps1 -Production` has run, then the browsers work again. Nothing else needs undoing
  (no `includeSubDomains`, no preload).
- **cert-manager** 1.21.3 from the official OCI chart (`oci://quay.io/jetstack/charts/cert-manager`), CRDs installed and kept by the
  chart. Two ClusterIssuers, `letsencrypt-staging` and `letsencrypt-production` (ACME HTTP-01 on Ingress class `traefik`). The ACME
  account e-mail comes from the user env var `PROCURE_ACME_EMAIL`, not from a repo.
- **The certificate:** Certificate `ingress-tls/procure-public` (ECDSA P-256, a new key on each renewal) for the three hosts, Secret
  `procure-public-tls`, and the TLSStore `ingress-tls/default` that makes it Traefik's default. It lives in its own namespace because
  `procure` denies all ingress traffic by default, which would block cert-manager's solver pod. Let's Encrypt certificates currently last 90 days
  (Let's Encrypt is shortening them); cert-manager renews when a third of the lifetime is left, and Test-PublicSurface wants at least 14
  days left.

What `envs/hosted/` sets: the public hosts; Keycloak's ingress serves only `/realms/procure/` and `/resources/` (no admin console, admin
API, master realm or welcome page; `publicRealms` in the platform chart); no Mailpit ingress; Argo CD without ingress (Keycloak sign-in; the admin login only as break-glass, through the tunnel);
the demo switches on (seed, nightly reset at 02:00 Africa/Johannesburg, one-click demo login, the "behind the scenes" panel); the
Assistant on Azure Foundry (`AI__Provider=AzureFoundry`, deployments `chat` and `embeddings`, no reasoning, no temperature, at least 256
output tokens per call, a daily token cap of 150 000 per organisation, sized below); `config.json` links the dashboard at `https://dashboard.daleabels.com`;
Postgres 20 GiB, MongoDB 5 GiB, RabbitMQ 2 GiB volumes.

**Rollouts on hosted keep the chart's default surge** (a new pod next to the old one, then the switch), unlike local's maxSurge 0. The
Postgres connection budget was sized for one pod per service (`MaxPoolSize` 8 x 17 pools fits `max_connections` 200), and during a surge
old and new pods hold pools at once. Measured on the VM (Task 12 fix round, 2026-11-08): 61 client connections idle, **85 at the peak**
of a restart of all 13 app Deployments at once, back to 63 when done. Pools open connections on demand, so the theoretical 2 x 193 needs
every pool of old and new pods full at the same moment. If it ever happens, a new pod fails its readiness while the old one keeps
serving (surge) and a failed migration hook stops the sync where Argo CD shows it; the fix is then `maxSurge: 0` in
`envs/hosted/procure-ai.yaml` or a higher `appHostResources.Postgres.MaxConnections` (a Postgres restart). Check after a deploy:
`kubectl exec postgres-0 -c postgres -- psql -U postgres -tAc "select count(*) from pg_stat_activity where backend_type='client backend'"`.

**IPv4 only (decided in Task 12).** The DNS names have A records only. Dual-stack would need a k3s reinstall with IPv6 cluster and
service CIDRs (a k3s cluster's address families cannot be changed in place), IPv6 host ports for Traefik, a second set of firewall
rules, and AAAA records, for a demo that reviewers open from office and home networks that all reach IPv4. Cost of the choice: an
IPv6-only visitor cannot open the demo. `Test-PublicSurface.ps1` fails if an AAAA record appears, because it would point at an address
where 80/443 do not answer.

### Checks against the public demo (from Dale's PC)

| Script (procure-ai-gitops) | What it proves |
|---|---|
| `tests/Test-PublicSurface.ps1` | Review Focus 2: A records only to the VM, no AAAA; a TCP connect scan of 3,846 ports (1-1024, the database, broker, Kubernetes, OTLP and dashboard ports, all NodePorts) finds only 22, 80 and 443; SSH offers only public keys; `http://` redirects to `https://`; each host's certificate is Let's Encrypt's and verifies for its name (openssl, not .NET: ESET re-signs TLS from pwsh on Dale's PC); Keycloak's `/admin`, master realm, welcome page and `/metrics` answer 404, also through path tricks (`/realms/procure/../master` sent as is, `%2e%2e`, `//`), and the procure realm's discovery 200; Argo CD and Mailpit host names and the bare address answer 404; the dashboard sends visitors to Keycloak's sign-in and its API answers 404; every host sends HSTS and no Server header names a version; an echo pod behind a throwaway Ingress sees this PC's public address in `X-Forwarded-For` (Review Focus 5; prints "matches", never the address) |
| `tests/Test-Dashboard.ps1 -Environment hosted` | The Aspire dashboard works for Dale (plan 08b): Microsoft Edge signs in at `https://dashboard.daleabels.com` as the demo admin, Structured logs and Traces show rows on every full page load, and the apps have rows from the last 10 minutes; Blazor's WebSocket accepts the dashboard's own Origin and refuses a foreign one. **After every push or deploy** (also part of `Test-FreshCluster.ps1 -Environment hosted -WatchOnly`); procure-ai `docs/kubernetes.md` "The Aspire dashboard: what makes it work" |
| `tests/Test-Smoke.ps1 -Environment hosted` | The local smoke checks against the public hosts (gateway, Keycloak, demo login, seeded data with the hosted admin key `DEMO_ADMIN_API_KEY_HOSTED`, dashboard sign-in by role, no pod but Traefik reaching the gateway or Keycloak with a spoofed `X-Forwarded-For`, pgaudit roles, realm drift) |
| `tests/Test-FreshCluster.ps1 -Environment hosted -WatchOnly [-Since <push time>]` | Every Application Synced and Healthy, every pod Ready and stable, then the smoke checks (the hosted cluster is never deleted by a test). With `-Since`, the deploy pushed at that time must have synced procure-ai, and its wave order is checked (platform first); without it a sync older than the watch is reported as "wave order NOT CHECKED" |
| `tests/Test-RestartKeepsData.ps1 -Environment hosted -Reboot` | Review Focus 4: a VM reboot; the cluster converges on its own within 15 minutes, Postgres shut down cleanly (no crash recovery in its log), every row, document and replication slot survives, every Debezium resumes, a new change reaches the data change log. The demo is live: a visitor or the 02:00 reset between the two counts gives a false FAIL, never a false PASS |
| `tests/Test-WalSlotsAdvance.ps1 -Environment hosted -IdleMinutes 360 -WatchWholeWindow` | Review Focus 1: after a WAL burst in an uncaptured database, every slot catches up, then stays below 64 MB of lag for the whole six idle hours |
| End-to-end tests, external target | The browser suite against `https://procure.daleabels.com` (procure-ai `docs/kubernetes.md`; for the hosted demo also `PROCUREAI_E2E_KEYCLOAK_URL=https://auth.daleabels.com` (the default, `auth.` + the gateway's host, does not fit; without it every Keycloak sign-in waits 60 s and fails), `PROCUREAI_E2E_USER_SECRETS_ID=procure-ai-hosted`, from which the tests then also take the admin key, never from `DEMO_ADMIN_API_KEY`, `KUBECONFIG` = the hosted kubeconfig and `PROCUREAI_E2E_KUBE_CONTEXT=procure-hosted`, and `-trait- "Model=Fake"` too: the one test of the Fake model's scripted answer cannot pass against the real model) |

### Size on the VM (measured 2026-11-07, `kubectl top`)

| When | Node (kubectl top node) | All pods | `free -m` |
|---|---|---|---|
| Argo CD only (Task 11) | 2046 MiB, 104m CPU | - | 1790 MiB used of 24026 |
| Whole demo, about 15 min after the first sync, idle | 7475 MiB (35 %), 1738m CPU (21 %, still settling) | 5774 MiB | 7674 MiB used, 16352 available |
| After the full E2E flow (28 browser tests, real model) | 7982 MiB (38 %), 1123m CPU (14 %) | 6220 MiB | - |

The largest pods: Keycloak about 660 MiB, Postgres about 260-300 MiB, each of the eight Debezium Servers about 250 MiB, MongoDB about
250 MiB, the Argo CD application controller about 260 MiB; cert-manager about 70 MiB in all. First sync of the whole demo from an empty
cluster: 11 min 09 s (node peak 7.18 GiB), Test-FreshCluster -WatchOnly. Disk: 13 GB used of 290 GB. Plenty of room on the 24 GB VM: no
size in `envs/hosted` needs raising.

**A reboot (Review Focus 4, `Test-RestartKeepsData.ps1 -Environment hosted -Reboot`, 2026-11-07):** SSH back after about 80 s, the whole
demo Synced/Healthy/Ready and the smoke checks passing about 5 minutes after the reboot command (limit 15). Postgres shut down cleanly
with the node ("database system was shut down at ..." on the next start, no crash recovery), every row, document and replication slot
kept, every Debezium Ready without a restart, and a new request reached the data change log 3 s after it was made. The certificate is a
Secret and survives as is.

A VM reboot leaves the pods the kubelet's graceful shutdown ended as `Failed` objects ("terminated in response to imminent node
shutdown"); their Deployments and StatefulSets replace them. They hold no resources. Remove them with
`kubectl delete pods -A --field-selector=status.phase=Failed` when they clutter the list.

## Argo CD on the VM (plan 08 Task 12c)

Argo CD is the one GUI for the live site (no Rancher, Headlamp or Grafana). It has no public address: it is reached through the SSH
tunnel and a `kubectl port-forward` on Dale's PC, at `http://127.0.0.1:8080`.

```powershell
cd procure-ai-gitops
./bootstrap/hosted/open-argocd.ps1          # tunnel (reused if open) + port-forward to 127.0.0.1:8080 + browser; prints where the passwords are
./bootstrap/hosted/open-argocd.ps1 -Close   # stops the port-forward, and the tunnel only if this script opened it
```

Who can sign in, and with what rights:

| Who | How | Rights |
|---|---|---|
| Dale (and anyone else in the group `argocd-admins` of realm `ops`) | "Log in via Keycloak": realm `ops`, client `argocd` (public, code flow + PKCE, no secret), password and then the authenticator app's 6-digit code, like Argo CD signing in with AD at work | `role:admin` |
| Any other user of realm `ops` (none exists unless someone makes one) | can sign in | nothing: no Application, project, log or action (`policy.default` is empty) |
| The public demo users (realm `procure`) | cannot sign in to Argo CD at all (another realm) | - |
| `admin` | break-glass, for when Keycloak is down: user `admin`, password in DEMO PASSWORDS.txt ("=== Argo CD ===") and Secret `argocd/argocd-initial-admin-secret` | `role:admin` |
| `claude` | API token only (no UI login): `Authorization: Bearer $env:ARGOCD_AUTH_TOKEN_HOSTED` on Argo CD's REST API; a new token from `./bootstrap/new-argocd-token.ps1 -Environment hosted` | `role:admin` |

Without the tunnel nothing of this is reachable. Claude also keeps `kubectl` (and `argocd --core`, which uses the kubeconfig) through the
tunnel, so no Argo CD admin function depends on Keycloak. The Keycloak sign-in is for the UI only: `argocd login --sso` would need the
CLI's callback `http://localhost:8085/auth/callback`, which the client deliberately does not allow (Claude uses its API token). Inside the
cluster argocd-server accepts no connection at all (a NetworkPolicy in `envs/hosted/argocd.yaml`: no pod can try the admin password; the
port-forward does not go through the pod network).

**Realm `ops`: Argo CD's own directory** (plan 08 fix wave ruling R6c). Argo CD signs people in from realm `ops`, not from the public
demo realm `procure`:

- The demo realm has shared users whose names are public, and no per-user lockout (it would let anyone keep them locked, ruling R6b).
  Access's service account `procure-access-admin` (`manage-users`, `manage-realm`) can change users there. None of that reaches realm
  `ops`: Access's roles are on realm `procure` only, so a compromise of the Access service no longer reaches Argo CD and the cluster.
- Realm `ops` holds only the client `argocd`, the group `argocd-admins` and the users someone makes with `add-argocd-admin.ps1` (none in
  the file). Its sign-in page is public at `https://auth.daleabels.com/realms/ops/` (the browser must reach it), so it has its own
  protection: a **temporary lockout after 5 wrong passwords** (1 minute, 1 minute more per further 5, at most 15 minutes, never
  permanent; two failures within a second lock for a minute too), and an **authenticator app for every user**. The realm binds its own
  sign-in flow (`ops browser`: password AND the OTP form, both REQUIRED, no "only if configured" condition), so a user without an
  authenticator (new, or one whose authenticator was deleted) must set one up before the sign-in completes. A password alone never
  signs in. Password grants (`ops direct grant`, e.g. through the built-in `admin-cli` client) need the code too; for a user without
  an authenticator Keycloak refuses them with a server error (it cannot run the setup there), which still issues no token. Configure
  OTP is also a default required action of new users. No self-registration, no password reset by e-mail. The rate limit of the whole
  Keycloak host applies as well (security baseline above).
- Proven live on 2026-11-08 (security review of the first version, which used Keycloak's conditional OTP): a throwaway user created
  without any required action got the authenticator-setup page after the right password, and no code; it signed in only after setting
  one up; after its OTP credential was deleted and its required actions cleared, the right password again led only to the setup page.
  Password grants without the code were refused in every state. The user was deleted.
- Proven live on 2026-11-08 with a throwaway user, before the push (realm applied by a one-off keycloak-config Job, Argo CD's issuer
  switched by hand with auto-sync paused, then restored):
  - Argo CD's `/auth/login` redirects to `.../realms/ops/protocol/openid-connect/auth`;
  - the first sign-in showed the authenticator-app setup;
  - without the group the user signed in and saw 0 Applications; after joining `argocd-admins`, password plus code showed all 5;
  - 5 wrong passwords locked the user, and the right password was refused while locked;
  - break-glass `admin` and the `claude` token still saw all 5 Applications;
  - the throwaway user was deleted.

The settings are in the GitOps repo: `argocd/values.yaml` (accounts, RBAC), `envs/hosted/argocd.yaml` (url, OIDC issuer
`https://auth.daleabels.com/realms/ops`) and `envs/hosted/apps.yaml` (admin login on, no ingress). Keycloak's side (realm, client
`argocd`, group `argocd-admins`, the `groups` claim, lockout, OTP) is in `infra/keycloak/ops-realm.json`. The keycloak-config Job applies
it next to `procure-realm.json` on every sync. Its redirect URIs follow the platform chart's `keycloak.argocdUrl` (`http://127.0.0.1:8080`
on hosted, so the port-forward must use port 8080 for the Keycloak sign-in).

### Your own Keycloak user for Argo CD

Claude does not create it: you pick your own user name and password. Keycloak's admin console cannot be used for this on the VM: the
public host serves only `/realms/procure/` and `/realms/ops/`, and the console always sends the browser to that public host (also
through a port-forward). So a script does it with Keycloak's own admin CLI inside the Keycloak pod, through the tunnel; it asks for the
password and never shows or stores it. You need an authenticator app on your phone (Microsoft Authenticator, Google Authenticator,
Aegis, 1Password, ...):

```powershell
cd procure-ai-gitops
./bootstrap/add-argocd-admin.ps1 -UserName <private name> -Email you@example.com -FirstName Dale -LastName Abels   # asks for the password twice
./bootstrap/hosted/open-argocd.ps1                                                           # then "Log in via Keycloak"
```

1. Pick a **user name that is published nowhere** (not an e-mail address, not your git or GitHub name): realm `ops` signs in by user
   name only, and a known name could be locked out by a stranger with wrong passwords. Run the first line with it; `-Email` is any
   address of yours (the profile needs one; it is not a sign-in name). It creates your user in realm `ops` and adds it to the group
   `argocd-admins`. **Sign in right after running it** (step 2): until the authenticator is set up, whoever knows the password could
   enrol theirs.
2. Open Argo CD with the second line and choose "Log in via Keycloak". The page says "Procure AI operations" (realm `ops`). Sign in with
   your user name and password.
3. The first time, Keycloak shows a QR code ("Mobile Authenticator Setup"). Scan it with the authenticator app, type the 6-digit code it
   shows, give the device a name, and submit. Argo CD then opens with admin rights.
4. From then on every sign-in asks for the password and then the app's current code.
5. Five wrong passwords in a row lock the user for a minute (longer if it goes on, at most 15 minutes). Wait and try again. Someone who
   knew your user name could keep it locked this way; break-glass `admin` and Claude's token still work then, and a new private user
   name fixes it.
6. **Lost phone (recovery):** break-glass `admin` still works. Delete the old authenticator and require a new one, through the tunnel
   with kcadm in the Keycloak pod, as the bootstrap admin (the same sign-in `add-argocd-admin.ps1` uses):
   `kcadm.sh get users/<id>/credentials -r ops` (note the `otp` entry's id), `kcadm.sh delete users/<id>/credentials/<otp id> -r ops`,
   then `kcadm.sh update users/<id> -r ops -s 'requiredActions=["CONFIGURE_TOTP"]'`. Running `./bootstrap/add-argocd-admin.ps1
   -UserName <your name>` again also re-adds Configure OTP to a user without an authenticator (appended to any other pending
   actions). Sign in right after the reset, for the same reason as in step 1. The next sign-in shows a new QR
   code. Even without the required action the flow insists on it (the OTP form is required), so deleting the credential never opens a
   password-only way in. A new password: `add-argocd-admin.ps1 ... -ResetPassword`.

- The sign-in name is `-UserName` (lower-case letters, digits, `.`, `_`, `-`; no `@`); e-mail sign-in is off in realm `ops`.
- The user is created enabled and e-mail verified, must set up the authenticator app at the first sign-in (the script also adds that
  required action to an existing user without one), and is added to `argocd-admins`. Argo CD's *User Info* then shows that group.
- The script refuses, before changing anything: an e-mail in a demo domain (`*.test`), an e-mail address as user name, a service
  account, one of the demo realm file's fixed user IDs, and an `-Email` that already belongs to a different user (no e-mail fallback).
  `tests/Test-AddArgocdAdmin.ps1` in the GitOps repo proves each refusal against the live Keycloak.
- The user exists in realm `ops` only, so it cannot sign in to the procure app (another realm).
- Run it again with `-ResetPassword` for a new password, or `-Remove` to take the admin role away (the user stays).
- Nothing in Git lists your user, so the keycloak-config Job (it re-applies the realm file on every sync) leaves it and its group
  membership alone.
- `-Environment local` does the same on the local k3d cluster.

### The live site is down: what to do

1. **Is it the demo switch?** `https://procure.daleabels.com/` shows the maintenance page when the demo is switched off on purpose
   (`docs/demo.md`). Nothing is broken then.
2. **Open Argo CD:** `./bootstrap/hosted/open-argocd.ps1`, "Log in via Keycloak" (password, then the authenticator app's code). If Keycloak itself is down (the sign-in page does not
   load), sign in as `admin` (break-glass, above).
3. **Find the broken part.** The *Applications* page: `root`, `argocd`, `platform`, `observability`, `procure-ai`. A red heart
   (*Degraded*) or a yellow circle (*Progressing* for minutes) is where to look. Open that Application; the tree shows each Deployment,
   StatefulSet and pod with its health. A failed sync (e.g. a migration hook) shows at the top (*Last sync*), with the message.
4. **Read why.** Click the pod: *Logs* (the container's output; pick the container), *Events* (image pull errors, OOMKilled, failed
   probes, evictions). `CrashLoopBackOff` plus the last log lines usually say it.
5. **Fix or restart.**
   - A pod stuck after a dependency came back (Keycloak, RabbitMQ, Postgres): on its Deployment, its menu -> *Restart*.
   - Out of sync after a hand change: *Sync* on the Application (auto-sync with self-heal does this by itself within a few minutes).
   - A failed migration hook: read the Job's log (step 4); fix it in Git (push) or roll back by setting an older `appRevision` in
     `envs/hosted/release.yaml` of the GitOps repo; Argo CD syncs it.
   - Postgres and Debezium: restart only when you must (Debezium resumes from its offsets; `Test-WalSlotsAdvance.ps1` checks the slots).
6. **Traces and logs across services:** `https://dashboard.daleabels.com` (Aspire dashboard; Keycloak sign-in, role `dashboard-viewer`,
   the demo admin user has it). *Structured logs* filtered by service and level, *Traces* for one request across services. Visitor IP
   addresses never reach it (the collector masks them in log bodies and drops client-address attributes). The connection churn of
   RabbitMQ and MongoDB and background traces (Wolverine's polling, readiness pings) are left out, so the caps hold hours of the apps'
   own logs and a day of request traces. If a page stays empty: `./tests/Test-Dashboard.ps1 -Environment hosted` in the GitOps repo.
7. **The VM itself** (Argo CD does not load at all, or every pod is pending): `./bootstrap/hosted/Test-HostBaseline.ps1` (SSH, k3s,
   firewall, listeners), then `kubectl top node` through the tunnel for memory. The Contabo panel has a VNC console and a reboot as the
   last resort; k3s and every app come back by themselves after a reboot.
8. Afterwards: `./bootstrap/hosted/open-argocd.ps1 -Close`.

**Holding the public demo during an incident** (plan 08 fix wave ruling R5). Every green push to procure-ai main bumps
`envs/hosted/release.yaml` and Argo CD deploys it, and a revision pinned by hand there holds only until the next publish. To freeze what
runs while you investigate, pause auto-sync, `root` first (it would otherwise put the child's sync policy back) and then `procure-ai`:

- in the Argo CD UI: open `root`, *Details*, *Sync policy*: *Disable auto-sync*; the same on `procure-ai`;
- or through the tunnel: `kubectl -n argocd patch application root --type merge -p '{"spec":{"syncPolicy":{"automated":null}}}'`, then
  the same with `procure-ai`.

Argo CD then shows new commits as *OutOfSync* but deploys nothing; the publish workflow keeps bumping `release.yaml`. To resume, pin the
revision you want in `envs/hosted/release.yaml` first (a full SHA whose images are in GHCR) if the newest is not wanted, then turn
auto-sync back on for `root` (UI, or `kubectl -n argocd patch application root --type merge -p
'{"spec":{"syncPolicy":{"automated":{"prune":true,"selfHeal":true}}}}'`; re-running `./bootstrap/install-argocd.ps1 -Environment hosted`
also restores it): its self-heal restores `procure-ai`'s sync policy from the apps chart. The switch that hides the demo from visitors is a different tool: the demo switch (`docs/demo.md`).

## Watching the demo: uptime and certificate

`procure-ai-gitops/.github/workflows/uptime.yml` runs **once a day** (07:17 South African time) while the repos are private, about 30 of
the 2 000 private Actions minutes a month (plan 08 fix wave ruling R2); going public moves it to every 30 minutes. It fails, and GitHub
e-mails Dale, when the gateway's readiness, the web app, Keycloak's realm or Access (the demo switch) does not answer, or when the
certificate of `procure`, `auth` or `dashboard.daleabels.com` has **fewer than 21 days** left. cert-manager renews at 30 days, so that
failure means renewal has been failing for 9 days and there is still a week before browsers refuse the site (HSTS: no click-through).
Let's Encrypt sends no expiry e-mails any more, so this is the only warning. Then: `kubectl -n ingress-tls describe
certificate,certificaterequest,order,challenge` through the tunnel (HTTP-01 must reach the solver through Traefik on port 80).
`bootstrap/hosted/Test-HostBaseline.ps1` checks the same certificate from inside (Ready, production issuer, more than 21 days).

## Keeping the node current (upgrades)

Unattended upgrades cover Ubuntu's packages only. k3s brings its own containerd, runc, Traefik, CoreDNS and flannel, so the
internet-facing node gets their fixes only through a k3s upgrade. **Once a month**, together with the pending-reboot check of
Test-HostBaseline, and whenever a k3s patch release fixes a CVE in one of them:

1. **Plan** (read-only): `./bootstrap/hosted/upgrade-k3s.ps1`. It shows the running version, the newest stable patch of the same minor
   and the embedded component versions of both (containerd, runc, Traefik, ...), and warns when Traefik's major or minor changes. Read the
   k3s release notes. A Traefik minor or major change: compare `bootstrap/hosted/traefik-config.yaml` (host ports, redirect, HSTS
   middleware, ingressEndpoint, resources) with the bundled chart's upgrade notes first. A new Kubernetes minor: only with `-Version`,
   one minor at a time, after the Kubernetes upgrade notes. Downgrades are refused.
2. **Upgrade** at a quiet time (not near the 02:00 reset): `./bootstrap/hosted/upgrade-k3s.ps1 -Version <vX.Y.Z+k3sN> -Apply`. It sets
   the version in the VM's `/usr/local/sbin/install-k3s.sh` and runs it (the release's own install script, which checks the binary's
   checksum and restarts k3s with the same config; the API is away for seconds, pods keep running), waits until the node is Ready on the
   new version, fetches the kubeconfig again (`new-hosted-cluster.ps1 -KubeconfigOnly`) and runs Test-HostBaseline. Its AppArmor probe
   proves the runc profile attaches to the new `data/<hash>/bin/runc` (the profile's path is a pattern, so a new hash needs no change).
3. **Pin it:** set `HostedK3sVersion` in `bootstrap/common.ps1` to the new version and commit (Test-HostBaseline's k3s check fails
   until then; cloud-init gets the pin from it on the next reinstall).
4. **Edge and app:** `./bootstrap/hosted/install-cert-manager.ps1 -Production` (re-applies `traefik-config.yaml` and waits for Traefik's
   rollout with host ports), `./tests/Test-PublicSurface.ps1`, `./tests/Test-FreshCluster.ps1 -Environment hosted -WatchOnly`.

**cert-manager** (pinned 1.21.3 in `install-cert-manager.ps1`): read the release's upgrade notes, render the new chart once with
`helm template cert-manager oci://quay.io/jetstack/charts/cert-manager --version <new> --set crds.enabled=true | kubeconform -strict
-ignore-missing-schemas`, then `./bootstrap/hosted/install-cert-manager.ps1 -Production -CertManagerVersion <new>` and change the default
in the script. The certificate stays (it is a Secret); Test-HostBaseline checks it afterwards.

**Argo CD** manages itself: raise `argocd.chartVersion` in `apps/values.yaml` of the GitOps repo (after the chart's and Argo CD's upgrade
notes), then `./tests/Test-RenderEnvs.ps1` (renders the new chart with both environments' values) and
`./tests/Test-ArgocdNetworkPolicy.ps1` (the hosted NetworkPolicies must still match the chart's), push. Argo CD syncs its own
Application to the new chart; check that all five Applications are Synced/Healthy afterwards.

**The kubeconfig on Dale's PC** holds k3s's admin client certificate (one year). k3s renews its certificates on a restart when they are expired or within 120 days
of expiry; the PC's copy then stops working. Test-HostBaseline fails when it has fewer than 30 days left; then restart k3s at a quiet time
(an upgrade or `sudo systemctl restart k3s`) and run `./bootstrap/hosted/new-hosted-cluster.ps1 -KubeconfigOnly`.

**One-time steps after the plan 08 fix wave push** (the changes are committed, the running VM needs them once; cloud-init has them for a
reinstall):

- Traefik's memory limit: `./bootstrap/hosted/install-cert-manager.ps1 -Production` (applies `traefik-config.yaml`; Traefik restarts once,
  a few seconds without ingress).
- ServiceLB off (nothing uses a LoadBalancer Service; checked 2026-11-08):

  ```powershell
  . ./bootstrap/common.ps1
  # 1. Append, only if the file has no disable: key yet. A missing final newline is added first (else disable: would join the last line).
  Invoke-HostedSsh -Command 'f=/etc/rancher/k3s/config.yaml; sudo grep -q "^disable:" $f && exit 0; [ -z "$(sudo tail -c1 $f)" ] || echo | sudo tee -a $f >/dev/null; printf "disable:\n  - servicelb\n" | sudo tee -a $f >/dev/null'
  # 2. Look at the end of the file before restarting: k3s does not start with a broken config, and the node is reachable only over SSH.
  Invoke-HostedSsh -Command 'sudo tail -n 4 /etc/rancher/k3s/config.yaml'
  # 3. Only when the last two lines are exactly "disable:" and "  - servicelb":
  Invoke-HostedSsh -Command 'sudo systemctl restart k3s'
  ```

  Then Test-HostBaseline: both checks pass (k3s without ServiceLB, Traefik memory limit).

## Backup copies and recovery

Everything the hosted setup needs that is not in a repo lives only on Dale's PC: the VM's SSH key, `known_hosts`, the Terraform state,
`host.json` and the kubeconfig (`%USERPROFILE%\.procure-ai\hosted\`), the deploy keys (`...\deploy-keys\`), the CAs, the two
user-secrets stores, the Contabo MFA recovery codes and DEMO PASSWORDS.txt. Losing the PC would leave only destructive paths (a
reinstall to adopt the VM, the VNC console, new secrets for everything).

**Backup copies** (Dale's decision 2026-11-08: plain files, no encryption, replacing ruling R4's encrypted archive):

```powershell
cd procure-ai-gitops
./bootstrap/hosted/backup-hosted.ps1          # mirrors every item into both folders below, then compares every file by SHA-256
./bootstrap/hosted/backup-hosted.ps1 -Check   # only compares: OK, MISMATCH, MISSING or EXTRA per file; exit 1 when not all OK
```

The copies go to `C:\Users\User\OneDrive\procure-ai secrets` (OneDrive, so off the PC) and `C:\Users\User\Documents\procure-ai secrets`,
each with the same layout: `DEMO PASSWORDS.txt`, `hosted\` (without its run-time files: `tunnel.pid`, logs, WAL-test files),
`deploy-keys\`, `local-ca\`, `user-secrets\procure-ai-hosted\`, `user-secrets\procure-ai-apphost\`, `contabo-recovery-codes.txt`
(from `%USERPROFILE%\Downloads`) and a `README.txt` naming each item's original. A file the original no longer has is removed from the
copy. The script prints names and counts only, never contents. **Run it after any change** to a secret, key, the Terraform state or
DEMO PASSWORDS.txt (a reinstall, `new-secrets.ps1` generating a value, a new deploy key or token).

Who can read the copies: before copying, the script gives each destination folder the same ACL as `%USERPROFILE%\.procure-ai`
(`Set-CurrentUserOnlyAcl`: only Dale's Windows user, inheritance off at the folder, everything inside inheriting that one rule), and both
modes check every file and subfolder and report any other identity (`ACCESS` lines, exit 1). OneDrive syncs as Dale's user, so syncing is
unaffected. A destination inside a git repository is refused before anything is created. Accepted risk (Dale, 2026-11-08, explained to
him): the OneDrive copy is unencrypted, so anyone who gets into Dale's Microsoft account can read every secret of the demo, and the
Contabo MFA recovery codes sit in the same folder as the Contabo password.

**Recovering the hosted setup** (the VM still runs; the PC or the folder is lost):

1. A PC with the tools of "Bringing it up", the env vars (the tokens are in DEMO PASSWORDS.txt), and both repos cloned.
2. Copy each item from `OneDrive\procure-ai secrets` back to its original (the `README.txt` there lists them): `hosted\`,
   `deploy-keys\` and `local-ca\` to `%USERPROFILE%\.procure-ai\`, `user-secrets\<id>\` to `%APPDATA%\Microsoft\UserSecrets\<id>\`,
   DEMO PASSWORDS.txt to the Desktop. `new-hosted-cluster.ps1 -KubeconfigOnly` (step 4) sets the hosted folder back to current-user-only
   permissions, which OpenSSH requires for the key.
3. With the SSH key back but the Terraform state lost (or too old): `./bootstrap/hosted/new-hosted-cluster.ps1 -Import -PlanOnly`, then
   without `-PlanOnly`. It finds the VM, its firewall, the SSH key secret and the three A records through the APIs and imports them into
   the state. **Nothing is reinstalled or recreated**: the plan guard refuses any create (except a DNS record that does not exist at
   all), any delete, and any change to the instance except `existing_instance_id` (which the provider's import leaves empty; its update
   reinstalls only for image, key, user data, root password or default user changes, and only reads the instance otherwise). Proven on
   2026-11-08 against an empty state: 6 imports, 0 to add, 0 to destroy, one in-place update of `existing_instance_id` only; a normal plan
   on the real state afterwards showed 0 changes.
4. Then `new-hosted-cluster.ps1 -KubeconfigOnly`, and `Test-HostBaseline.ps1`.

Without the copies: the SSH key is gone, so only the Contabo VNC console (root password in DEMO PASSWORDS.txt, if that survived) reaches
the VM, and the hosted secrets must be generated again (`new-secrets.ps1 -Environment hosted` makes new ones; every database, Keycloak
and Debezium secret then differs from what the volumes hold). The realistic way back is then a deliberate `-Reinstall`: the demo data is
reseeded every night, so nothing that matters is lost, only the time to bring it up again.

## What it costs, and how to stop it

Expected monthly cost (plan 08 Task 13, 2026-11-08). Rates: ECB 2026-11-06, USD 1 = R 16,54, EUR 1 = USD 1.1165 = R 18,47. Rand amounts in the en-ZA format (R 258,50).

| Item | Per month | USD | ZAR |
|---|---|---|---|
| Contabo Cloud VPS 8 (order 15654477, monthly term) | EUR 14.00 (0 % VAT on the invoice), Contabo's list price | 15.63 | R 258,50 |
| Domain `daleabels.com` (Cloudflare Registrar, USD 11.17 a year, auto-renew, expires 2027-11-07) | one twelfth | 0.93 | R 15,40 |
| Azure Foundry, **expected** (about 1.0 M tokens: the nightly re-seed of Acme and Globex, ~16 000 tokens a night, 0.5 M a month, and ~10 questions a day of ~1 800 tokens, 0.5 M a month) | gpt-5-mini $0.25 in / $2.00 out per 1 M, embeddings $0.022 per 1 M | 0.40-0.80 | R 7-13 |
| Azure Foundry, **worst case under the caps** (every organisation at its daily cap every day, below) | 14 M tokens at $0.60 per 1 M | 8.40 | R 139 |
| GitHub (repos, Actions inside the free 2 000 private minutes, of which the daily uptime check takes about 30; free once public), GHCR images | free | 0 | 0 |
| Cloudflare DNS, Let's Encrypt certificate, cert-manager | free | 0 | 0 |
| Contabo firewall, SSH key secret, IPv4 and IPv6 (included with the VPS) | free | 0 | 0 |
| **Total, expected** | | **about 17.00-17.40** | **about R 281-287** |
| **Total, worst case** | | **about 25.00** | **about R 413** |

**The AI cost is capped by the app, not by Azure.** Each organisation may use `AI:DailyTokenCapPerTenant` tokens a day, **150 000 on the
live demo** (`envs/hosted/procure-ai.yaml` in the GitOps repo, with the arithmetic). Measured on the live demo (2026-11-08): a policy
question costs 1 700-1 900 tokens (guard classifier and answer), one with a tool call about 3 300, a question the guard blocks about 300;
the nightly re-seed of one organisation (policy ingestion and past purchases) about 8 000. So the cap allows about 40-80 questions a day per
organisation. There are three organisations (Acme, Globex, Demo); self-registration is off, and when on it adds users to Demo, never new
organisations. Worst case: 3 x 150 000 x 30 days plus the two seeds, which are counted but never stopped (procure-ai `docs/assistant.md`,
"Daily token cap"), is about 14 M tokens. Priced as if 20 % were output (measured: about 5 %), that is $0.60 per 1 M and **$8.40 a month**,
under the Azure budget alert. Every model call reserves an estimate (its prompt plus its output limit) before it is made and settles it
with the real usage after, so calls already in flight count when the cap is checked, and an answer whose visitor closed the tab
mid-stream keeps its reservation (it may have been billed); a refused prompt (400, Azure's content filter) keeps the prompt part, as
Azure bills its evaluation; only a call Azure does not process (401, 403, 404, 429, an open breaker, no connection) gives it back. A
question is checked against the cap when it starts and again right before its answer call, but questions that passed before the cap was
reached still run to the end (each about 2-3 k tokens): a scripted burst of questions fired together at the cap can still add the
questions already past their checks, at most what the `chat` deployment's 50 requests a minute let through (a few US cents per
organisation and day, bounded by the per-user quota). Per user the Assistant also allows 50 questions a
day, and the demo login 30 sign-ins a minute per visitor address (300 a minute in all).

**Azure guards.** The budget `procure-ai-10usd` on the subscription: USD 10 a month, e-mails to the Azure account's address at 50 %,
80 % and 100 % of the actual cost and at 100 % of the forecast (80 % added 2026-11-08; spend that day USD 0.13). Cost Management cannot
send a test e-mail: only a real threshold proves the e-mail. The deployments, checked through Azure Resource Manager and Azure's
`x-ratelimit-limit-*` answer headers on 2026-11-08: `chat` (gpt-5-mini, GlobalStandard, capacity 50: 50 000 tokens and 50 requests a
minute) and `embeddings` (text-embedding-3-small, DataZoneStandard, capacity 100: 100 000 tokens a minute). The Azure account is a **Free Trial** (USD 200 credit, spending limit
on) that ends about **2026-12-07**. Until then the credit pays and nothing is billed to the card. After that, Azure stops the deployments
unless the account is upgraded to pay-as-you-go, which is Dale's call. Without the upgrade, switch the live demo to the Fake model first
(below), so the Assistant gives scripted answers instead of errors.

### Pausing the costs

| To stop | Do this | Saves | Undo |
|---|---|---|---|
| Visitors using the demo, incl. its AI | The demo switch off (`PUT /api/v1/system/availability`, `docs/demo.md` "Switching the demo off"): every visitor sees the maintenance page | Every visitor's AI cost; the nightly re-seed still runs (about 16 000 tokens, under USD 0.01 a night) | The switch on |
| All Azure AI cost, demo still usable | In the GitOps repo `envs/hosted/procure-ai.yaml`, `AI__Provider: Fake` (and remove the `AI__Endpoint` and `AI__ApiKey` lines), push: Argo CD rolls the Assistant to the keyless Fake model (scripted answers, no tokens). The stored vectors came from the other model and match poorly, so after switching (either way) the nightly re-seed embeds the policy again; replace it on the policy documents screen to have it at once | All of Azure | The old values back, push |
| Azure entirely | Delete the two deployments (`az cognitiveservices account deployment delete -g <resource group> -n procureai-foundry-862eee --deployment-name chat`, then the same with `--deployment-name embeddings`; `-n` is the Foundry account, not the deployment; the resource group comes from `az cognitiveservices account list --query "[?name=='procureai-foundry-862eee'].resourceGroup" -o tsv`); first switch to Fake as above. A deployment costs nothing while unused (pay per token), so this only matters when the account is closed | Nothing extra while unused | Recreate them as in plan 08 Task 9 (`chat` gpt-5-mini 2025-08-07 GlobalStandard 50, `embeddings` text-embedding-3-small DataZoneStandard 100) |
| The VPS | Cancel it in the Contabo panel (below) | EUR 14 a month | A new order, then `new-hosted-cluster.ps1` and the steps above |
| The domain | Turn auto-renew off in the Cloudflare dashboard (it then lapses on 2027-11-07) | USD 11.17 a year | Auto-renew on |

The VPS line in more detail: the Contabo API lists one add-on on the instance, ID 1501 ×1, from the order itself. Contabo's public product list does not name the
ID. It costs nothing extra: the invoice (EUR 14.00) equals the list price of Cloud VPS 8 with no setup fee, so it is one of the order's
included choices (region, storage type or the no-setup option). Terraform never adds or removes add-ons (`ignore_changes`). Dale can
confirm its name in the panel under the order.

Contabo bills the VPS **until the end of its term**: a monthly contract from 2026-11-07, renewed every month. Stopping, deleting the
DNS records, or reinstalling the VM does **not** stop the billing. Only a cancellation does, and that is Dale's call:

- **Cancel:** in the Contabo customer panel (my.contabo.com, the VPS, "Cancel"), at least **4 weeks** before the end of the current
  monthly term (Contabo's terms). As a consumer, Dale also has a 14-day right of withdrawal from 2026-11-07.
- Terraform never cancels. `terraform destroy` fails on `prevent_destroy`, and `./bootstrap/hosted/new-hosted-cluster.ps1 -Destroy`
  removes only the DNS records, after the same plan check. The firewall stays (the VM keeps running k3s), and so do the SSH key secret and
  the instance. Afterwards it checks through both APIs that the records are gone, the firewall is still active on the instance, and the
  instance is **not** cancelled. Use it to take the demo off its names, not to save money; a normal run brings the records back.
- No backups, snapshots, extra IPs, object storage or private networks are used (each would bill extra). The demo data is reseeded every
  night, so a reinstall loses nothing that matters.

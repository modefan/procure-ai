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
- **Memory reservations** (24 GB VM): kube 1.5 GiB, system 512 MiB, eviction below 1 GiB free.
- **Secrets.** Kubernetes Secrets are encrypted at rest (k3s `secrets-encryption`).
- **Nothing secret in a repo.** No Terraform state, kubeconfig, SSH key or Secret is in any repo. They live in
  `%USERPROFILE%\.procure-ai\hosted\` on Dale's PC (current user only) and are listed by name in DEMO PASSWORDS.txt. The hosted Secrets
  come from their own user-secrets store `procure-ai-hosted` (`new-secrets.ps1 -Environment hosted`), so the public demo shares no secret
  with the local setup. The registry pull Secret uses a read-only GHCR token, not the GitHub CLI token.

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
./bootstrap/install-argocd.ps1 -Environment hosted    # Argo CD (no admin user, no ingress) and the root Application
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

The Argo CD UI has no public address. Open the tunnel, then run
`kubectl --kubeconfig %USERPROFILE%\.procure-ai\hosted\kubeconfig --context procure-hosted -n argocd port-forward svc/argocd-server 8080:80`.

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
- **cert-manager** 1.21.3 from the official OCI chart (`oci://quay.io/jetstack/charts/cert-manager`), CRDs installed and kept by the
  chart. Two ClusterIssuers, `letsencrypt-staging` and `letsencrypt-production` (ACME HTTP-01 on Ingress class `traefik`). The ACME
  account e-mail comes from the user env var `PROCURE_ACME_EMAIL`, not from a repo.
- **The certificate:** Certificate `ingress-tls/procure-public` (ECDSA P-256, a new key on each renewal) for the three hosts, Secret
  `procure-public-tls`, and the TLSStore `ingress-tls/default` that makes it Traefik's default. It lives in its own namespace because
  `procure` denies all ingress traffic by default, which would block cert-manager's solver pod. Let's Encrypt certificates currently last 90 days
  (Let's Encrypt is shortening them); cert-manager renews when a third of the lifetime is left, and Test-PublicSurface wants at least 14
  days left.

What `envs/hosted/` sets: the public hosts; Keycloak's ingress serves only `/realms/procure/` and `/resources/` (no admin console, admin
API, master realm or welcome page; `publicRealms` in the platform chart); no Mailpit ingress; Argo CD without admin user and ingress;
the demo switches on (seed, nightly reset at 02:00 Africa/Johannesburg, one-click demo login, the "behind the scenes" panel); the
Assistant on Azure Foundry (`AI__Provider=AzureFoundry`, deployments `chat` and `embeddings`, no reasoning, no temperature, at least 256
output tokens per call, the app's default daily token cap per tenant); `config.json` links the dashboard at `https://dashboard.daleabels.com`;
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

## What it costs, and how to stop it

| Item | Per month |
|---|---|
| Contabo Cloud VPS 8 (order 15654477, monthly term) | **EUR 14.00** (0 % VAT on the invoice), which is Contabo's list price for Cloud VPS 8 |
| Contabo firewall, SSH key secret, IPv4 and IPv6 included with the VPS, Cloudflare DNS records | free |
| Let's Encrypt certificate, cert-manager | free |

The Contabo API lists one add-on on the instance, ID 1501 ×1, from the order itself. Contabo's public product list does not name the
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

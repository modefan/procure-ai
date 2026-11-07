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
| Kubernetes | **k3s** v1.35.10+k3s1 (single node, the local cluster's minor), with Traefik and ServiceLB as shipped | Same distribution as the local k3d cluster. |
| Network | Contabo firewall: inbound **only TCP 22, 80, 443** (IPv4 and IPv6), then Contabo's own "block all traffic" rule. No ICMP: the VM does not answer ping, which does not mean it is down. | The Kubernetes API, the kubelet and kube-proxy's health port listen only on the node address `10.0.1.2`. That address is on a dummy interface, because Contabo's private networks are a paid add-on. |
| GitOps | Argo CD on the VM, root Application on `envs/hosted/` | Same app of apps as local. |
| Models | Azure Foundry (Task 9) | |
| DNS | Cloudflare (`daleabels.com`): A and AAAA for `procure`, `auth` and `dashboard`, DNS only (not proxied), TTL 300 s | Kept by the same Terraform root (official `cloudflare/cloudflare` provider, account-owned token with DNS Write on this zone only). The apex and other records are not touched. |

**Why Contabo and not Hetzner.** Plan 08 chose Hetzner (cx43, EUR 15.99 a month). But every affordable 16 GB type (cx43, cx53, cax31,
cpx41) was sold out in all EU locations for the whole day of 2026-11-07. The next types in stock cost EUR 69 or more. The Terraform for
Hetzner is in git history (`infra/hetzner`, before the move to `infra/hosted`).

## Security baseline (checked by `bootstrap/hosted/Test-HostBaseline.ps1`)

- **SSH.** There is one non-root sudo user, `procure`, who logs in with an ed25519 key only. The private half of the key is only on Dale's
  PC. There is no root login and no password or keyboard-interactive login, and only that user may log in. Agent and X11 forwarding are
  off; only local port forwarding is allowed (the API tunnel). fail2ban (journal of `ssh.service`, including `sshd-session`) bans
  addresses that keep failing.
- **Contabo's own cloud-config.** On every boot it sets `PermitRootLogin yes` in `sshd_config` and sets the root password. Our
  `sshd_config.d/00-procure-hardening.conf` is read first and wins. The root password works only on Contabo's VNC console, which the
  Contabo API cannot switch off; Dale can switch it off in the panel. Its `pkill -HUP sshd` fails on Ubuntu 26.04, so `cloud-init status`
  says "error" on every boot. The bootstrap accepts that one known error and nothing else.
- **Updates.** Unattended security upgrades are on. The VM does **not** reboot by itself: a reboot restarts the whole demo, so it is a
  deliberate step. Test-HostBaseline warns when an update waits for a reboot. Then reboot at a quiet time (`ssh ... sudo systemctl
  reboot`). The kubelet gives pods 60 s to stop (checked: the pods ended "terminated in response to imminent node shutdown"), and k3s
  comes back on its own within about 30 s.
- **The Kubernetes API (6443)** is not reachable from the internet: it listens on the node address only, and the firewall drops it
  anyway. kubectl and helm reach it through `bootstrap/hosted/open-tunnel.ps1` (ssh `-L 127.0.0.1:16443:10.0.1.2:6443`).
- **Graceful shutdown.** The kubelet shuts pods down gracefully when the VM shuts down: `shutdownGracePeriod` 60 s, 20 s of it for
  critical pods. logind waits 75 s. Its drop-in must sort after unattended-upgrades' own 30 s file, or the kubelet's shutdown manager does
  not start.
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
./bootstrap/hosted/new-hosted-cluster.ps1 -PlanOnly   # plan + money guard: SSH key secret, the adopted VPS, firewall, 6 DNS records
./bootstrap/hosted/new-hosted-cluster.ps1 -Reinstall  # first time only: adopts and REINSTALLS the VPS, cloud-init, kubeconfig, tunnel, namespaces
./bootstrap/hosted/new-hosted-cluster.ps1             # any later time: applies firewall/DNS changes, re-checks SSH, cloud-init, the cluster
./bootstrap/new-secrets.ps1 -Environment hosted       # Secrets from the procure-ai-hosted user secrets (generated on the first run)
./bootstrap/install-argocd.ps1 -Environment hosted    # Argo CD (no admin user, no ingress) and the root Application
./bootstrap/hosted/Test-HostBaseline.ps1              # the checks above, from the PC
./bootstrap/hosted/open-tunnel.ps1 -Close             # close the tunnel when done
```

Every script is safe to run again. `new-hosted-cluster.ps1` refuses a plan that would:

- order an instance (a create without `existing_instance_id = 203735351`);
- delete the instance (for the Contabo provider, delete means *cancel*; `prevent_destroy` blocks it as well);
- add any other paid Contabo resource or add-on, or other DNS records;
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

## What it costs, and how to stop it

| Item | Per month |
|---|---|
| Contabo Cloud VPS 8 (order 15654477, monthly term, no add-ons bought) | **EUR 14.00** (0 % VAT on the invoice) |
| Contabo firewall, SSH key secret, IPv4 and IPv6 included with the VPS, Cloudflare DNS records | free |

Contabo bills the VPS **until the end of its term**: a monthly contract from 2026-11-07, renewed every month. Stopping, deleting the
DNS records, or reinstalling the VM does **not** stop the billing. Only a cancellation does, and that is Dale's call:

- **Cancel:** in the Contabo customer panel (my.contabo.com, the VPS, "Cancel"), at least **4 weeks** before the end of the current
  monthly term (Contabo's terms). As a consumer, Dale also has a 14-day right of withdrawal from 2026-11-07.
- Terraform never cancels. `terraform destroy` fails on `prevent_destroy`, and `./bootstrap/hosted/new-hosted-cluster.ps1 -Destroy` only
  removes the free parts: the DNS records, the firewall and the SSH key secret. It then forgets the instance in the state. Afterwards it
  checks through both APIs that the records and the firewall are gone and that the instance is still there and **not** cancelled. Use it to
  take the demo off its names, not to save money.
- No backups, snapshots, extra IPs, object storage or private networks are used (each would bill extra). The demo data is reseeded every
  night, so a reinstall loses nothing that matters.

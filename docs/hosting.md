# Hosting the live demo

The live demo runs on one virtual machine at Hetzner Cloud with k3s, deployed by Argo CD from
[procure-ai-gitops](https://github.com/modefan/procure-ai-gitops), the same GitOps flow as the local cluster
([kubernetes.md](kubernetes.md)). The VM itself is code: Terraform in `procure-ai-gitops/infra/hetzner/` creates it, cloud-init installs
k3s on its first boot, and the bootstrap scripts in `procure-ai-gitops/bootstrap/hosted/` run from Dale's PC through an SSH tunnel.

## What runs where

| Piece | Choice | Why |
|---|---|---|
| VM | Hetzner Cloud **cx43** (8 shared vCPU x86, 16 GB, 160 GB disk) in Falkenstein (`fsn1`), Ubuntu 26.04 LTS | The full stack needs about 9 GiB plus Argo CD; x86 runs the published images as they are. |
| Kubernetes | **k3s** v1.35.10+k3s1 (single node; the local cluster's minor), Traefik and ServiceLB as shipped | Same distribution as the local k3d cluster. |
| Network | Hetzner firewall: inbound **only TCP 22, 80, 443** (IPv4 and IPv6); a private network `10.0.1.0/24` | The Kubernetes API listens only on the private address `10.0.1.2:6443`. |
| GitOps | Argo CD on the VM, root Application on `envs/hosted/` | Same app of apps as local. |
| Models | Azure Foundry (Task 9) | |
| DNS | Cloudflare (`daleabels.com`) | Records are set in Task 12. |

## Security baseline (checked by `bootstrap/hosted/Test-HostBaseline.ps1`)

- SSH: one non-root sudo user `procure`, key only (ed25519, private half only on Dale's PC). No root login, no password or
  keyboard-interactive login, only that user, no agent/X11 forwarding; only local port forwarding (the API tunnel). fail2ban bans
  addresses that keep failing.
- Unattended security upgrades are on; the VM does **not** reboot by itself (a reboot restarts the whole demo, so it is a deliberate step).
- The Kubernetes API (6443) is not reachable from the internet: it listens on the private address only, and the firewall drops it anyway.
  kubectl and helm reach it through `bootstrap/hosted/open-tunnel.ps1` (ssh `-L 127.0.0.1:16443:10.0.1.2:6443`).
- The kubelet shuts pods down gracefully when the VM shuts down: `shutdownGracePeriod` 60 s, 20 s of it for critical pods (logind waits
  75 s). Memory reservations as on the local cluster (kube 1 GiB, system 256 MiB, eviction below 1 GiB free).
- Kubernetes Secrets are encrypted at rest (k3s `secrets-encryption`).
- No Terraform state, kubeconfig, SSH key or Secret is in any repo: they live in `%USERPROFILE%\.procure-ai\hosted\` on Dale's PC
  (current user only) and are listed by name in DEMO PASSWORDS.txt. The hosted Secrets come from their own user-secrets store
  `procure-ai-hosted` (`new-secrets.ps1 -Environment hosted`), so the public demo shares no secret with the local setup; the registry
  pull Secret uses a read-only GHCR token, not the GitHub CLI token.

## Bringing it up (from Dale's PC)

Needs `terraform`, `kubectl`, `helm`, Windows OpenSSH, `gh` and the .NET SDK, plus the user env vars `HCLOUD_TOKEN` (Hetzner, project
`procure-ai`) and `GHCR_READ_TOKEN`.

```powershell
cd procure-ai-gitops
./bootstrap/hosted/new-hosted-cluster.ps1 -PlanOnly   # plan + money guard: only the SSH key, network, firewall and one cx43 in fsn1
./bootstrap/hosted/new-hosted-cluster.ps1             # apply (asks), wait for cloud-init, kubeconfig, tunnel, namespaces
./bootstrap/new-secrets.ps1 -Environment hosted       # Secrets from the procure-ai-hosted user secrets (generated on the first run)
./bootstrap/install-argocd.ps1 -Environment hosted    # Argo CD (no admin user, no ingress) and the root Application
./bootstrap/hosted/Test-HostBaseline.ps1              # the checks above, from the PC
./bootstrap/hosted/open-tunnel.ps1 -Close             # close the tunnel when done
```

Every script is safe to run again. `new-hosted-cluster.ps1` refuses a plan that would create anything else, change the server type or
location, or replace the server (all its data) unless `-AllowReplace` is given. Changes to `cloud-init.yaml` do not touch a running
server (Terraform ignores them); make the change on the VM over SSH too (`ssh -i %USERPROFILE%\.procure-ai\hosted\procure-hosted_ed25519
procure@<ip>`, using the `known_hosts` file there).

The Argo CD UI has no public address: open the tunnel, then
`kubectl --kubeconfig %USERPROFILE%\.procure-ai\hosted\kubeconfig --context procure-hosted -n argocd port-forward svc/argocd-server 8080:80`.

## What it costs, and how to pause it

| Item | Per month (checked through the Hetzner API, 2026-11-07) |
|---|---|
| cx43 in fsn1 | EUR 15.99 net, EUR 18.39 with 15 % VAT |
| Primary IPv4 | EUR 0.50 net, EUR 0.58 with VAT |
| Firewall, private network, SSH key, IPv6, 20 TB traffic | free |
| **Total** | **about EUR 19 per month** (billed by the hour, EUR 0.0294 + 0.0009 per hour) |

Hetzner bills a server for every hour it **exists**, also while it is switched off. To pause the cost, delete it:

```powershell
cd procure-ai-gitops/infra/hetzner
terraform destroy -var "ssh_public_key_path=$env:USERPROFILE\.procure-ai\hosted\procure-hosted_ed25519.pub" -var "k3s_version=v1.35.10+k3s1"
```

Everything on the VM is lost (the demo data is reseeded every night anyway). `new-hosted-cluster.ps1` and the steps above bring it back
in about 15 minutes; the new server gets a new IPv4, so the DNS records must follow (Task 12). No backups, snapshots, volumes or load
balancers are used (each would bill extra).

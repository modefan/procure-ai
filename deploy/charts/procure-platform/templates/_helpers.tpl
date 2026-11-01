{{/* Labels of one component: include "procure-platform.labels" (dict "root" $ "name" "postgres") */}}
{{- define "procure-platform.labels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/part-of: procure-ai
app.kubernetes.io/component: platform
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .root.Chart.Name .root.Chart.Version }}
{{- end }}

{{- define "procure-platform.selector" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
{{- end }}

{{/* One of our images: [<registry>/]<name>:<global.imageTag>. include "procure-platform.ownImage" (dict "root" $ "image" "procure-ai-postgres") */}}
{{- define "procure-platform.ownImage" -}}
{{- $tag := required "global.imageTag is required (the commit SHA, or local-<sha7> for images built on the PC)" .root.Values.global.imageTag -}}
{{- if .root.Values.global.imageRegistry -}}
{{ printf "%s/%s:%s" (trimSuffix "/" .root.Values.global.imageRegistry) .image $tag }}
{{- else -}}
{{ printf "%s:%s" .image $tag }}
{{- end -}}
{{- end }}

{{/* Requests and limits from an appHostResources entry: include "procure-platform.resources" (dict "root" $ "limits" .Values.appHostResources.Postgres) */}}
{{- define "procure-platform.resources" -}}
{{- $memory := int .limits.MemoryMb -}}
limits:
  memory: {{ printf "%dMi" $memory }}
  cpu: {{ .limits.Cpus | quote }}
requests:
  memory: {{ printf "%dMi" (div (mul $memory (int .root.Values.requests.memoryPercent)) 100) }}
  cpu: {{ .root.Values.requests.cpu | quote }}
{{- end }}

{{/* Pod-level security: non-root with a fixed uid/gid, seccomp RuntimeDefault, no service account token. */}}
{{- define "procure-platform.podSecurity" -}}
automountServiceAccountToken: false
{{- with .root.Values.global.imagePullSecrets }}
imagePullSecrets:
  {{- range . }}
  - name: {{ . }}
  {{- end }}
{{- end }}
securityContext:
  runAsNonRoot: true
  runAsUser: {{ .uid }}
  runAsGroup: {{ .gid }}
  {{- if .fsGroup }}
  fsGroup: {{ .fsGroup }}
  fsGroupChangePolicy: OnRootMismatch
  {{- end }}
  seccompProfile:
    type: RuntimeDefault
{{- end }}

{{/* Container-level security: read-only root filesystem, no privilege escalation, every capability dropped. */}}
{{- define "procure-platform.containerSecurity" -}}
securityContext:
  readOnlyRootFilesystem: true
  allowPrivilegeEscalation: false
  capabilities:
    drop:
      - ALL
{{- end }}

{{/* Hook annotations of a one-off setup Job: Argo CD PostSync (deleted before the next sync's run), and the Helm equivalent for
     `helm upgrade --install` (Argo CD ignores the Helm ones when its own are present). */}}
{{- define "procure-platform.postSyncHook" -}}
argocd.argoproj.io/hook: PostSync
argocd.argoproj.io/hook-delete-policy: BeforeHookCreation
helm.sh/hook: post-install,post-upgrade
helm.sh/hook-delete-policy: before-hook-creation
{{- end }}

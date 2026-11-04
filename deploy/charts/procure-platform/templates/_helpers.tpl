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

{{/* Annotations of a platform bootstrap Job (replica-set initiation, the cdc exchange). It must not wait for the apps: Debezium only
     becomes Ready after the apps' migrations, and those need this bootstrap (a PostSync / post-install hook deadlocked a fresh install,
     plan 07 Task 5 fix round 2). Argo CD: a Sync hook in wave 1, after the platform's StatefulSets (wave 0) are up and before Debezium
     (wave 2); re-run on every sync (BeforeHookCreation). Helm: no hook, so `helm upgrade --install --wait` creates it with everything
     else and the Job retries until its server answers; "procure-platform.bootstrapJobName" makes each Helm revision a new Job. */}}
{{- define "procure-platform.bootstrapJob" -}}
argocd.argoproj.io/hook: Sync
argocd.argoproj.io/hook-delete-policy: BeforeHookCreation
argocd.argoproj.io/sync-wave: "1"
{{- end }}

{{/* <base>-<release revision>: a Job's pod template cannot change in place, and a completed Job would not run again on `helm upgrade`.
     Argo CD renders with revision 1, so its hook keeps one name. include "procure-platform.bootstrapJobName" (dict "root" $ "base" "x") */}}
{{- define "procure-platform.bootstrapJobName" -}}
{{- printf "%s-%d" .base (int .root.Release.Revision) -}}
{{- end }}

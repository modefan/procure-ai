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

{{/* One of our images: [<registry>/]<name>:<tag>, the tag from global.imageTags.<name without procure-ai-> (the platform images are
tagged by the content of their build context, plan 07 Task 6), else global.imageTag.
include "procure-platform.ownImage" (dict "root" $ "image" "procure-ai-postgres") */}}
{{- define "procure-platform.ownImage" -}}
{{- $tag := index (.root.Values.global.imageTags | default dict) (trimPrefix "procure-ai-" .image) | default .root.Values.global.imageTag -}}
{{- $tag = required "global.imageTag is required (the commit SHA, or local-<sha7> for images built on the PC)" $tag -}}
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

{{/* The key patterns of the operator's read-only Redis user (operatorReaders.redis.keys) as ACL rules "~a ~b". Fails on an empty list, a
     pattern with a blank, or one that would match the gateway's data-protection key ring or session tickets (procure:bff:*): with those a
     reader could forge a session for any signed-in user (plan 08b Task 4 fix round 1 I1). */}}
{{- define "procure-platform.redisKeyPatterns" -}}
{{- if not . }}{{ fail "operatorReaders.redis.keys must list at least one key pattern" }}{{ end }}
{{- $rules := list }}
{{- range . }}
{{- if regexMatch `\s` . }}{{ fail (printf "operatorReaders.redis.keys: %q holds a blank" .) }}{{ end }}
{{- $re := printf "^%s$" (replace "?" "." (replace "*" ".*" (replace "." `\.` .))) }}
{{- range list "procure:bff:data-protection-keys" "procure:bff:session:x" "procure:bff:user:x" "procure:bff:switch-ticket:x" }}
{{- if regexMatch $re . }}{{ fail (printf "operatorReaders.redis.keys: a pattern matches the gateway's secret key %s" .) }}{{ end }}
{{- end }}
{{- $rules = append $rules (printf "~%s" .) }}
{{- end }}
{{- join " " $rules }}
{{- end }}

{{/* The app's name (workload and Service). */}}
{{- define "procure-app.name" -}}
{{- required "name is required (the AppHost resource name, e.g. requests-api)" .Values.name -}}
{{- end }}

{{/* The migration Job's name: <name without -api>-migrate unless migration.name is set. */}}
{{- define "procure-app.migrationName" -}}
{{- .Values.migration.name | default (printf "%s-migrate" (trimSuffix "-api" (include "procure-app.name" .))) -}}
{{- end }}

{{- define "procure-app.labels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/part-of: procure-ai
app.kubernetes.io/component: {{ .component | default "app" }}
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .root.Chart.Name .root.Chart.Version }}
{{- end }}

{{- define "procure-app.selector" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
{{- end }}

{{/* [<registry>/]<repository>:<tag>; tag: the given one, else global.imageTags.<repository without procure-ai-> (e.g. postgres: the
platform image's content tag), else global.imageTag. include "procure-app.imageRef" (dict "root" $ "repository" "procure-ai-x" "tag" "") */}}
{{- define "procure-app.imageRef" -}}
{{- $tag := .tag | default (index (.root.Values.global.imageTags | default dict) (trimPrefix "procure-ai-" (toString .repository))) | default .root.Values.global.imageTag -}}
{{- $tag = required "global.imageTag is required (the commit SHA, or local-<sha7> for images built on the PC)" $tag -}}
{{- $repository := required "image.repository is required (e.g. procure-ai-requests-api)" .repository -}}
{{- if .root.Values.global.imageRegistry -}}
{{ printf "%s/%s:%s" (trimSuffix "/" .root.Values.global.imageRegistry) $repository $tag }}
{{- else -}}
{{ printf "%s:%s" $repository $tag }}
{{- end -}}
{{- end }}

{{- define "procure-app.image" -}}
{{- include "procure-app.imageRef" (dict "root" . "repository" .Values.image.repository "tag" .Values.image.tag) -}}
{{- end }}

{{/* Pod-level settings shared by the Deployment and the migration Job. */}}
{{- define "procure-app.podSecurity" -}}
automountServiceAccountToken: false
{{- with .Values.global.imagePullSecrets }}
imagePullSecrets:
  {{- range . }}
  - name: {{ . }}
  {{- end }}
{{- end }}
securityContext:
  runAsNonRoot: true
  runAsUser: {{ .Values.runAsUser }}
  runAsGroup: {{ .Values.runAsUser }}
  seccompProfile:
    type: RuntimeDefault
{{- end }}

{{- define "procure-app.containerSecurity" -}}
securityContext:
  readOnlyRootFilesystem: true
  allowPrivilegeEscalation: false
  capabilities:
    drop:
      - ALL
{{- end }}

{{/* Init containers waiting for each waitFor URL (curl, every 5 s, until it answers 2xx). */}}
{{- define "procure-app.waitFor" -}}
{{- range $i, $url := .Values.waitFor }}
- name: {{ printf "wait-%d" $i }}
  image: {{ $.Values.global.curlImage | quote }}
  imagePullPolicy: {{ $.Values.global.imagePullPolicy }}
  command: [sh, -c]
  args:
    - until curl -fsS -o /dev/null --max-time 5 "$URL"; do echo "waiting for $URL"; sleep 5; done
  env:
    - name: URL
      value: {{ $url | quote }}
  resources:
    {{- toYaml $.Values.global.helperResources | nindent 4 }}
  {{- include "procure-app.containerSecurity" $ | nindent 2 }}
{{- end }}
{{- end }}

{{/* Volumes every pod of the app mounts: /tmp (read-only root filesystem), the local CA, the runtime config.json. */}}
{{- define "procure-app.volumes" -}}
- name: tmp
  emptyDir:
    sizeLimit: 128Mi
{{- if .Values.caTrust.enabled }}
- name: procure-ca
  configMap:
    name: {{ .Values.caTrust.configMap }}
{{- end }}
{{- if .Values.configJson }}
- name: config-json
  configMap:
    name: {{ printf "%s-config" (include "procure-app.name" .) }}
{{- end }}
{{- end }}

{{- define "procure-app.volumeMounts" -}}
- name: tmp
  mountPath: /tmp
{{- if .Values.caTrust.enabled }}
- name: procure-ca
  mountPath: {{ .Values.caTrust.mountPath }}
  readOnly: true
{{- end }}
{{- if .Values.configJson }}
- name: config-json
  mountPath: {{ .Values.configJsonPath }}
  subPath: config.json
  readOnly: true
{{- end }}
{{- end }}

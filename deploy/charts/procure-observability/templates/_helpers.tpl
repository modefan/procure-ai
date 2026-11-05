{{/* Labels of one component: include "procure-observability.labels" (dict "root" $ "name" "aspire-dashboard") */}}
{{- define "procure-observability.labels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/part-of: procure-ai
app.kubernetes.io/component: observability
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .root.Chart.Name .root.Chart.Version }}
{{- end }}

{{- define "procure-observability.selector" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
{{- end }}

{{/* The collector's pods (labels of the opentelemetry-collector chart). */}}
{{- define "procure-observability.collectorSelector" -}}
app.kubernetes.io/name: opentelemetry-collector
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

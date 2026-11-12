{{/* Labels of one component: include "procure-ops.labels" (dict "root" $ "name" "pgadmin") */}}
{{- define "procure-ops.labels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/part-of: procure-ai
app.kubernetes.io/component: operator
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .root.Chart.Name .root.Chart.Version }}
{{- end }}

{{- define "procure-ops.selector" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
{{- end }}

{{/* Pod-level security: non-root with a fixed uid/gid, seccomp RuntimeDefault, no service account token.
include "procure-ops.podSecurity" (dict "uid" 5050 "gid" 0) */}}
{{- define "procure-ops.podSecurity" -}}
automountServiceAccountToken: false
securityContext:
  runAsNonRoot: true
  runAsUser: {{ .uid }}
  runAsGroup: {{ .gid }}
  fsGroup: {{ .gid }}
  seccompProfile:
    type: RuntimeDefault
{{- end }}

{{/* Container-level security: read-only root filesystem, no privilege escalation, every capability dropped. */}}
{{- define "procure-ops.containerSecurity" -}}
securityContext:
  readOnlyRootFilesystem: true
  allowPrivilegeEscalation: false
  capabilities:
    drop:
      - ALL
{{- end }}

{{/* The Traefik middlewares of a tool's Ingress: rate limit, security headers, then the tool's own extras (e.g. basic auth).
include "procure-ops.middlewares" (dict "root" $ "extra" (list "redis-insight-basic-auth")) */}}
{{- define "procure-ops.middlewares" -}}
{{- $ns := .root.Release.Namespace -}}
{{- $names := concat (list "operator-ratelimit" "operator-headers") (.extra | default list) -}}
{{- $refs := list -}}
{{- range $names }}{{ $refs = append $refs (printf "%s-%s@kubernetescrd" $ns .) }}{{ end -}}
{{ join "," $refs }}
{{- end }}

{{/* One tool's Ingress: include "procure-ops.ingress" (dict "root" $ "name" "pgadmin" "host" .Values.hosts.pgadmin "extra" (list)) */}}
{{- define "procure-ops.ingress" -}}
{{- if not .host }}{{ fail (printf "hosts for %s is required" .name) }}{{ end }}
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ .name }}
  labels:
    {{- include "procure-ops.labels" (dict "root" .root "name" .name) | nindent 4 }}
  annotations:
    traefik.ingress.kubernetes.io/router.middlewares: {{ include "procure-ops.middlewares" . }}
spec:
  ingressClassName: {{ .root.Values.ingressClassName }}
  # No secretName: Traefik's default certificate (Let's Encrypt on hosted, the local CA's locally), as for every other host.
  tls:
    - hosts:
        - {{ .host }}
  rules:
    - host: {{ .host }}
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: {{ .name }}
                port:
                  name: http
---
{{ include "procure-ops.originGuard" (dict "root" .root "name" .name "host" .host "middleware" "operator-foreign-origin") }}
{{- end }}

{{/* Cross-site requests to an operator host are refused (plan 08b Task 4 fix round 1, review M5): a browser that holds a tool's login
     (basic auth is cached and sent cross-site; a session cookie) would otherwise carry a request another site makes, and a "simple" POST
     with a text/plain body needs no CORS preflight (RabbitMQ's management API published a message that way, checked on the cluster).
     Browsers send an Origin header on every cross-site POST, PUT, DELETE and WebSocket upgrade; this router takes every request whose
     Origin is not the host's own (priority above the Ingress's router) and answers 403 through a deny-all ipAllowList middleware, so it
     never reaches the tool. Requests without an Origin (address bar, curl, same-origin GETs) and the tool's own pages are not affected.
     include "procure-ops.originGuard" (dict "root" $ "name" "pgadmin" "host" "pgadmin.example" "middleware" "operator-foreign-origin") */}}
{{- define "procure-ops.originGuard" -}}
apiVersion: traefik.io/v1alpha1
kind: IngressRoute
metadata:
  name: {{ .name }}-foreign-origin
  labels:
    {{- include "procure-ops.labels" (dict "root" .root "name" .name) | nindent 4 }}
spec:
  entryPoints: [websecure]
  routes:
    - kind: Rule
      match: {{ printf "Host(`%s`) && HeaderRegexp(`Origin`, `.+`) && !Header(`Origin`, `https://%s`)" .host .host | quote }}
      priority: 100000
      middlewares:
        - name: {{ .middleware }}
      services:
        - name: noop@internal
          kind: TraefikService
  tls: {}
{{- end }}

{{/* A tool's Service (port http -> the container port named http). */}}
{{- define "procure-ops.service" -}}
apiVersion: v1
kind: Service
metadata:
  name: {{ .name }}
  labels:
    {{- include "procure-ops.labels" . | nindent 4 }}
spec:
  selector:
    {{- include "procure-ops.selector" . | nindent 4 }}
  ports:
    - name: http
      port: 80
      targetPort: http
      protocol: TCP
{{- end }}

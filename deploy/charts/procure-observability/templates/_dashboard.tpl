{{/*
One Aspire dashboard: Service, Deployment, the forwarded-proto Middleware and the Ingress (templates/dashboard.yaml, dashboard-direct.yaml).
  dict "root" $ "name" <Service and Deployment name> "host" <public host> "direct" <true: Traefik basic auth in front, false: Keycloak>
       "middlewares" <extra Traefik middleware references for the Ingress, after the forwarded-proto one>
*/}}
{{- define "procure-observability.dashboard" -}}
{{- $root := .root }}
{{- $name := .name }}
{{- $host := .host }}
{{- $direct := .direct }}
{{- $d := $root.Values.dashboard }}
{{- $labels := dict "root" $root "name" $name }}
{{- $middlewares := prepend (.middlewares | default list) (printf "%s-%s-forwarded-proto@kubernetescrd" $root.Release.Namespace $name) }}
{{- /* With the proxy (dashboard.proxy, plan 08b finding 2) the port named http is nginx's, and the dashboard's frontend is "frontend". */}}
{{- $frontendPort := ternary "frontend" "http" $d.proxy.enabled }}
# The standalone Aspire dashboard: frontend on 18888 behind Traefik (Keycloak OIDC, dashboard-viewer only), OTLP gRPC on 18889 for the
# collector (no key; network-policies.yaml lets only the collector in). Telemetry is kept in memory and lost on restart (CLAUDE.md).
# With dashboard.proxy.enabled, nginx in the same pod (port http, 8080) sits in front of the frontend and adds the blank-table workaround.
apiVersion: v1
kind: Service
metadata:
  name: {{ $name }}
  labels:
    {{- include "procure-observability.labels" $labels | nindent 4 }}
spec:
  selector:
    {{- include "procure-observability.selector" $labels | nindent 4 }}
  ports:
    - name: http
      port: 18888
      targetPort: http
      protocol: TCP
    - name: otlp-grpc
      port: 18889
      targetPort: otlp-grpc
      protocol: TCP
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ $name }}
  labels:
    {{- include "procure-observability.labels" $labels | nindent 4 }}
spec:
  replicas: 1
  # One pod at a time: in-memory telemetry and data protection keys are per pod, and the node has no room for a surge pod.
  strategy:
    type: Recreate
  selector:
    matchLabels:
      {{- include "procure-observability.selector" $labels | nindent 6 }}
  template:
    metadata:
      labels:
        {{- include "procure-observability.labels" $labels | nindent 8 }}
      {{- if $d.proxy.enabled }}
      annotations:
        # A changed proxy config or script restarts the pod (subPath mounts are not updated in place).
        checksum/proxy: {{ print ($root.Files.Get "files/dashboard-proxy.conf") ($root.Files.Get "files/virtualize-nudge.js") | sha256sum }}
      {{- end }}
    spec:
      automountServiceAccountToken: false
      securityContext:
        runAsNonRoot: true
        runAsUser: {{ $d.uid }}
        runAsGroup: {{ $d.uid }}
        seccompProfile:
          type: RuntimeDefault
      containers:
        - name: dashboard
          image: {{ $d.image | quote }}
          imagePullPolicy: {{ $d.imagePullPolicy }}
          ports:
            - name: {{ $frontendPort }}
              containerPort: 18888
              protocol: TCP
            - name: otlp-grpc
              containerPort: 18889
              protocol: TCP
          env:
            # All pod addresses (the kubelet's tcpSocket probes need the pod IP). With the proxy, only the NetworkPolicy keeps other pods
            # from reaching this port directly (Traefik may reach port http = nginx only); with networkPolicies.enabled false the frontend
            # is reachable inside the cluster without nginx (accepted: it still needs a sign-in).
            - name: ASPNETCORE_URLS
              value: http://+:18888
            - name: DOTNET_DASHBOARD_OTLP_ENDPOINT_URL
              value: http://+:18889
            # The address the browser uses (Traefik, TLS). Blazor's WebSocket only passes the dashboard's Origin check because the
            # Ingress forces X-Forwarded-Proto: https (Middleware aspire-dashboard-forwarded-proto below).
            - name: Dashboard__Frontend__PublicUrl
              value: {{ printf "https://%s" $host }}
{{- if $direct }}
            # Break-glass (plan 08b Task 4): no sign-in in the dashboard itself; Traefik asks for its own login first (basicAuth middleware,
            # templates/dashboard-direct.yaml), no Keycloak, so it works while Keycloak is down. Not the dashboard's BrowserToken mode:
            # 13.6.2 writes the token into its start-up log, and behind a proxy that forwards https it sets the sign-in cookie under the
            # https name but reads the http one (AspireDashboardCookieManager), so a token sign-in never held (seen live 2026-11-10).
            # Only Traefik reaches the pod's http port (NetworkPolicy allow-traefik-to-<name>), so nothing gets past that login.
            - name: Dashboard__Frontend__AuthMode
              value: Unsecured
{{- else }}
            # Browser sign-in with Keycloak (code flow + PKCE, confidential client).
            - name: Dashboard__Frontend__AuthMode
              value: OpenIdConnect
            - name: Authentication__Schemes__OpenIdConnect__Authority
              value: {{ printf "https://%s/realms/procure" $root.Values.hosts.keycloak }}
            - name: Authentication__Schemes__OpenIdConnect__ClientId
              value: {{ $d.oidc.clientId }}
            - name: Authentication__Schemes__OpenIdConnect__ClientSecret
              valueFrom:
                secretKeyRef:
                  name: {{ $d.oidc.secretName }}
                  key: {{ $d.oidc.secretKey }}
            # The dashboard fixes the callback path to "/" and asks for "openid profile"; the realm's aspire-dashboard client allows
            # https://<host>/ and has the (mapper-less) profile scope as optional (procure-realm.json).
            - name: Dashboard__Frontend__OpenIdConnect__RequiredClaimType
              value: {{ $d.oidc.requiredClaimType }}
            - name: Dashboard__Frontend__OpenIdConnect__RequiredClaimValue
              value: {{ $d.oidc.requiredClaimValue }}
            - name: Dashboard__Frontend__OpenIdConnect__NameClaimType
              value: name
            - name: Dashboard__Frontend__OpenIdConnect__UsernameClaimType
              value: preferred_username
{{- end }}
            # TLS ends at Traefik: trust X-Forwarded-Proto/Host so the redirect URI is https://<host>/ (the dashboard's fixed callback path).
            # It reads its own switch, not ASPNETCORE_FORWARDEDHEADERS_ENABLED (with that one Keycloak refused the http:// redirect URI).
            - name: ASPIRE_DASHBOARD_FORWARDEDHEADERS_ENABLED
              value: "true"
            # OTLP without an API key: only the collector may connect (network-policies.yaml).
            - name: Dashboard__Otlp__AuthMode
              value: Unsecured
            # No dashboard API (telemetry API under /api/telemetry, MCP server): not needed, and it has its own auth mode. 13.6.2 reads
            # both keys; its start-up log says "Dashboard API disabled: True".
            - name: Dashboard__Api__Disabled
              value: "true"
            - name: Dashboard__Api__Enabled
              value: "false"
            - name: Dashboard__TelemetryLimits__MaxLogCount
              value: {{ $d.telemetryLimits.maxLogCount | quote }}
            - name: Dashboard__TelemetryLimits__MaxTraceCount
              value: {{ $d.telemetryLimits.maxTraceCount | quote }}
            - name: Dashboard__TelemetryLimits__MaxMetricsCount
              value: {{ $d.telemetryLimits.maxMetricsCount | quote }}
            - name: DOTNET_GCHeapHardLimit
              value: {{ $d.heapHardLimit | quote }}
            # Keycloak's certificate is issued by the local CA (plan 07 ruling 8).
            - name: SSL_CERT_DIR
              value: {{ printf "/etc/ssl/certs:%s" $d.caTrust.mountPath }}
          resources:
            {{- toYaml $d.resources | nindent 12 }}
          securityContext:
            readOnlyRootFilesystem: true
            allowPrivilegeEscalation: false
            capabilities:
              drop:
                - ALL
          startupProbe:
            tcpSocket:
              port: {{ $frontendPort }}
            periodSeconds: 2
            failureThreshold: 60
          readinessProbe:
            tcpSocket:
              port: {{ $frontendPort }}
            periodSeconds: 10
          livenessProbe:
            tcpSocket:
              port: {{ $frontendPort }}
            periodSeconds: 20
          volumeMounts:
            - name: tmp
              mountPath: /tmp
            - name: procure-ca
              mountPath: {{ $d.caTrust.mountPath }}
              readOnly: true
        {{- if $d.proxy.enabled }}
        {{- $p := $d.proxy }}
        # nginx in front of the frontend (files/dashboard-proxy.conf): passes everything through and adds /_procure/virtualize-nudge.js
        # to every page. Started without the image's entrypoint scripts (they rewrite files under /etc/nginx, which is read-only here).
        - name: proxy
          image: {{ $p.image | quote }}
          imagePullPolicy: {{ $p.imagePullPolicy }}
          command: ["nginx", "-g", "daemon off;"]
          ports:
            - name: http
              containerPort: 8080
              protocol: TCP
          resources:
            {{- toYaml $p.resources | nindent 12 }}
          securityContext:
            runAsUser: {{ $p.uid }}
            runAsGroup: {{ $p.uid }}
            readOnlyRootFilesystem: true
            allowPrivilegeEscalation: false
            capabilities:
              drop:
                - ALL
          readinessProbe:
            tcpSocket:
              port: http
            periodSeconds: 10
          livenessProbe:
            tcpSocket:
              port: http
            periodSeconds: 20
          volumeMounts:
            - name: proxy-tmp
              mountPath: /tmp
            - name: proxy
              mountPath: /etc/nginx/nginx.conf
              subPath: nginx.conf
              readOnly: true
            - name: proxy
              mountPath: /etc/procure-dashboard-proxy/virtualize-nudge.js
              subPath: virtualize-nudge.js
              readOnly: true
        {{- end }}
      volumes:
        # The dashboard's SQLite telemetry database (values.yaml dashboard.tmpSizeLimit).
        - name: tmp
          emptyDir:
            sizeLimit: {{ $d.tmpSizeLimit }}
        - name: procure-ca
          configMap:
            name: {{ $d.caTrust.configMap }}
        {{- if $d.proxy.enabled }}
        - name: proxy-tmp
          emptyDir:
            sizeLimit: 16Mi
        - name: proxy
          configMap:
            name: aspire-dashboard-proxy
        {{- end }}
---
# Blazor's WebSocket (/_blazor) behind Traefik (plan 08b finding 1). Traefik sends X-Forwarded-Proto: wss on a WebSocket upgrade over TLS
# (ws on plain http). With forwarded headers trusted, the dashboard (13.6) then sees Request.Scheme "wss", and its WebSocketOriginValidator
# compares the browser's Origin (https://<host>) with scheme + host of the request: "https" is not "wss", so EVERY circuit got 403
# "Origin not allowed" and the pages stayed without data (the earlier comment here, "falls back to long polling", was wrong). TLS always ends at
# Traefik for this host and the Ingress serves the websecure (https) entry point only (hosted also redirects http; local has no redirect,
# so plain http gets 404 there instead of a page told it is https): the scheme the browser used is always https, and the Middleware sets
# exactly that on every request of the dashboard's Ingress. The origin check stays meaningful: a foreign Origin (another scheme or host) still gets 403
# (tests: helm-unittest here, the live check procure-ai-gitops tests/Test-Dashboard.ps1).
apiVersion: traefik.io/v1alpha1
kind: Middleware
metadata:
  name: {{ $name }}-forwarded-proto
  labels:
    {{- include "procure-observability.labels" $labels | nindent 4 }}
spec:
  headers:
    customRequestHeaders:
      X-Forwarded-Proto: https
---
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ $name }}
  labels:
    {{- include "procure-observability.labels" $labels | nindent 4 }}
  annotations:
    # https only: the forced X-Forwarded-Proto: https is then true by construction.
    traefik.ingress.kubernetes.io/router.entrypoints: websecure
    traefik.ingress.kubernetes.io/router.middlewares: {{ $middlewares | join "," }}
spec:
  ingressClassName: {{ $root.Values.ingressClassName }}
  # No secretName: Traefik serves its default certificate (TLSStore `default`: the local CA's from bootstrap/new-local-ca.ps1; Let's Encrypt's on hosted).
  tls:
    - hosts:
        - {{ $host }}
  rules:
    - host: {{ $host }}
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: {{ $name }}
                port:
                  name: http
{{- end }}

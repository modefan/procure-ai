{{/* The pod both demo-data workloads run: ProcureAi.DemoData <command> against the gateway inside the cluster, with the admin key. */}}
{{- define "procure-ai.demoDataPod" -}}
{{- $root := .root }}
metadata:
  labels:
    app.kubernetes.io/name: {{ .name }}
    app.kubernetes.io/instance: {{ $root.Release.Name }}
    app.kubernetes.io/part-of: procure-ai
    app.kubernetes.io/component: demo-data
spec:
  restartPolicy: Never
  automountServiceAccountToken: false
  {{- with $root.Values.global.imagePullSecrets }}
  imagePullSecrets:
    {{- range . }}
    - name: {{ . }}
    {{- end }}
  {{- end }}
  securityContext:
    runAsNonRoot: true
    runAsUser: 1654
    runAsGroup: 1654
    seccompProfile:
      type: RuntimeDefault
  containers:
    - name: demo-data
      image: {{ .image | quote }}
      imagePullPolicy: {{ $root.Values.global.imagePullPolicy }}
      args:
        - {{ .command }}
      env:
        - name: DOTNET_GCHeapHardLimit
          value: {{ $root.Values.global.dotnet.heapHardLimit | quote }}
        - name: DOTNET_gcServer
          value: {{ $root.Values.global.dotnet.gcServer | quote }}
        - name: DOTNET_GCConserveMemory
          value: {{ $root.Values.global.dotnet.gcConserveMemory | quote }}
        - name: DemoData__GatewayUrl
          value: {{ tpl $root.Values.demoData.gatewayUrl $root | quote }}
        - name: DemoData__SimulatorUrl
          value: {{ tpl $root.Values.demoData.simulatorUrl $root | quote }}
        - name: DemoData__AllowUntrustedServerCertificate
          value: "false"
        - name: DemoData__AdminApiKey
          valueFrom:
            secretKeyRef:
              name: procure-api-keys
              key: admin-api-key
        - name: OTEL_EXPORTER_OTLP_ENDPOINT
          value: {{ index $root.Values.global.sharedEnv.telemetry "OTEL_EXPORTER_OTLP_ENDPOINT" | quote }}
        - name: OTEL_SERVICE_NAME
          value: demo-data
      resources:
        {{- toYaml $root.Values.demoData.resources | nindent 8 }}
      securityContext:
        readOnlyRootFilesystem: true
        allowPrivilegeEscalation: false
        capabilities:
          drop:
            - ALL
      volumeMounts:
        - name: tmp
          mountPath: /tmp
  volumes:
    - name: tmp
      emptyDir:
        sizeLimit: 64Mi
{{- end }}

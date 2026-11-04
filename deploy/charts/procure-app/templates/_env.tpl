{{/*
The env of an app's container, sorted by name: the opted-in global groups (sharedEnv), service discovery (calls), the .NET caps, the CA
trust, then the app's own env/secretEnv, then the caller's extra (the migration's). A later source wins over an earlier one for the same
name, plain or secret. Plain values go through `tpl` (so values can say '{{ .Values.global.hosts.keycloak }}'); secrets are
secretKeyRefs, never optional.
  include "procure-app.env" (dict "root" $ "env" <extra plain> "secretEnv" <extra secret>)
*/}}
{{- define "procure-app.env" -}}
{{- $root := .root -}}
{{- $values := $root.Values -}}
{{- $plain := dict -}}
{{- $secret := dict -}}
{{- range $group := $values.sharedEnv -}}
  {{- $p := index ($values.global.sharedEnv | default dict) $group -}}
  {{- $s := index ($values.global.sharedSecretEnv | default dict) $group -}}
  {{- if and (not $p) (not $s) -}}
    {{- fail (printf "%s: shared env group %s is not defined in global.sharedEnv or global.sharedSecretEnv" $values.name $group) -}}
  {{- end -}}
  {{- range $k, $v := ($p | default dict) }}{{ $_ := set $plain $k $v }}{{ $_ := unset $secret $k }}{{ end -}}
  {{- range $k, $v := ($s | default dict) }}{{ $_ := set $secret $k $v }}{{ $_ := unset $plain $k }}{{ end -}}
{{- end -}}
{{- range $values.calls -}}
  {{- $_ := set $plain (printf "services__%s__http__0" .) (printf "http://%s:%v" . $values.global.port) -}}
{{- end -}}
{{- if $values.dotnet.enabled -}}
  {{- $d := $values.global.dotnet | default dict -}}
  {{- $_ := set $plain "DOTNET_GCHeapHardLimit" ($values.dotnet.heapHardLimit | default $d.heapHardLimit) -}}
  {{- $_ := set $plain "DOTNET_gcServer" ($values.dotnet.gcServer | default $d.gcServer) -}}
  {{- $_ := set $plain "DOTNET_GCConserveMemory" ($values.dotnet.gcConserveMemory | default $d.gcConserveMemory) -}}
{{- end -}}
{{- if $values.caTrust.enabled -}}
  {{- $_ := set $plain "SSL_CERT_DIR" (printf "/etc/ssl/certs:%s" $values.caTrust.mountPath) -}}
{{- end -}}
{{- range $source := list (dict "p" $values.env "s" $values.secretEnv) (dict "p" .env "s" .secretEnv) -}}
  {{- range $k, $v := ($source.p | default dict) }}{{ $_ := set $plain $k $v }}{{ $_ := unset $secret $k }}{{ end -}}
  {{- range $k, $v := ($source.s | default dict) }}{{ $_ := set $secret $k $v }}{{ $_ := unset $plain $k }}{{ end -}}
{{- end -}}
{{- range $name := concat (keys $plain) (keys $secret) | sortAlpha }}
{{- if hasKey $secret $name }}
{{- $ref := get $secret $name }}
- name: {{ $name }}
  valueFrom:
    secretKeyRef:
      name: {{ required (printf "%s: secretEnv %s needs secret" $values.name $name) $ref.secret }}
      key: {{ required (printf "%s: secretEnv %s needs key" $values.name $name) $ref.key }}
{{- else }}
- name: {{ $name }}
  value: {{ tpl (toString (get $plain $name)) $root | quote }}
{{- end }}
{{- end }}
{{- end }}

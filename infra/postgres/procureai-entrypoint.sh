#!/bin/bash
# The server log on the console: logging_collector=on (needed for the csvlog the pgaudit summary reads) takes the server's stderr into
# files, so docker logs (and the Aspire dashboard) would only see the start. This wrapper starts a follower that streams the current
# stderr log file (as $PGDATA/current_logfiles names it, so it moves on when the file rotates at midnight) to the container's stdout,
# then execs the image's normal entrypoint with the same arguments.
#
# After the exec the follower is a child of the postmaster, which treats a child that ends with an error or a signal as a crashed backend
# and restarts every session. So it ignores the usual signals and never ends (it dies with the container), and errors inside are swallowed.
set -euo pipefail

follow_server_log() {
    set +euo pipefail
    trap "" HUP INT TERM QUIT USR1 USR2 PIPE
    local current="" tail_pid="" file="" path=""
    while true; do
        file=""
        if [ -n "${PGDATA:-}" ] && [ -r "$PGDATA/current_logfiles" ]; then
            file="$(awk '$1 == "stderr" { print $2 }' "$PGDATA/current_logfiles" 2>/dev/null || true)"
        fi
        if [ -n "$file" ] && [ "$file" != "$current" ]; then
            if [ -n "$tail_pid" ]; then
                kill "$tail_pid" 2>/dev/null || true
                wait "$tail_pid" 2>/dev/null || true
            fi
            case "$file" in
                /*) path="$file" ;;
                *) path="$PGDATA/$file" ;;
            esac
            tail -n +1 -F "$path" 2>/dev/null &
            tail_pid=$!
            current="$file"
        fi
        sleep 1
    done
}

follow_server_log &
exec docker-entrypoint.sh "$@"

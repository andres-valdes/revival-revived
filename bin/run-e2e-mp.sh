#!/bin/sh
# Autonomous multiplayer E2E: launches two Valheim+BepInEx processes that connect
# over the patched CustomSocket (raw TCP) backend on localhost. The host downs
# itself; the client joins, validates ragdoll position sync, and revives it.
# Each process runs the in-game harness and quits itself; we collect both result
# files.
set -u

VALHEIM="$HOME/.local/share/Steam/steamapps/common/Valheim"
HOST_RES="$VALHEIM/BepInEx/e2e-host-results.txt"
CLI_RES="$VALHEIM/BepInEx/e2e-client-results.txt"
HOST_OUT="${RR_E2E_HOST_LOG:-/tmp/valheim-e2e-host.log}"     # process stdout (BepInEx + Valheim ZLog)
CLI_OUT="${RR_E2E_CLI_LOG:-/tmp/valheim-e2e-client.log}"
HOST_E2E="/tmp/valheim-e2e-host-harness.log"                 # reliable per-role harness log
CLI_E2E="/tmp/valheim-e2e-client-harness.log"
PORT="${RR_E2E_PORT:-2456}"

rm -f "$HOST_RES" "$CLI_RES" "$HOST_OUT" "$CLI_OUT" "$HOST_E2E" "$CLI_E2E"
cd "$VALHEIM" || exit 99

export SteamAppId=892970 SteamGameId=892970
COMMON_ARGS="-screen-fullscreen 0 -screen-width 640 -screen-height 480"

echo "=== launching HOST ==="
env RR_E2E=1 RR_E2E_ROLE=host RR_E2E_PORT="$PORT" RR_E2E_WORLD=e2e_mp \
    RR_E2E_RESULTS="$HOST_RES" RR_E2E_LOG="$HOST_E2E" \
    timeout 340 ./run_bepinex.sh ./valheim.x86_64 $COMMON_ARGS > "$HOST_OUT" 2>&1 &
HOST_PID=$!

# Wait until the host opens its TCP listener (from the reliable harness log).
i=0
until grep -q "opened CustomSocket" "$HOST_E2E" 2>/dev/null || ! kill -0 "$HOST_PID" 2>/dev/null; do
    i=$((i + 1))
    [ "$i" -gt 150 ] && { echo "!! host did not open socket within ${i}s"; break; }
    sleep 1
done
echo "=== host socket opened (waited ${i}s); launching CLIENT ==="

env RR_E2E=1 RR_E2E_ROLE=client RR_E2E_PORT="$PORT" RR_E2E_HOST=127.0.0.1 RR_E2E_WORLD=e2e_mp \
    RR_E2E_RESULTS="$CLI_RES" RR_E2E_LOG="$CLI_E2E" \
    timeout 320 ./run_bepinex.sh ./valheim.x86_64 $COMMON_ARGS > "$CLI_OUT" 2>&1 &
CLI_PID=$!

wait "$HOST_PID"; echo "=== host exit: $? ==="
wait "$CLI_PID"; echo "=== client exit: $? ==="

echo "=== HOST RESULTS ==="; cat "$HOST_RES" 2>/dev/null || echo "(no host results)"
echo "=== CLIENT RESULTS ==="; cat "$CLI_RES" 2>/dev/null || echo "(no client results)"

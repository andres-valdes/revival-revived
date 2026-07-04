#!/bin/sh
# Revive-loop stress E2E: two processes over the CustomSocket backend; the host
# goes down repeatedly, the client revives it with realistic held input, and
# both sides scan ZDOMan for leaked marker ZDOs after every cycle.
set -u

VALHEIM="$HOME/.local/share/Steam/steamapps/common/Valheim"
HOST_RES="$VALHEIM/BepInEx/e2e-host-results.txt"
CLI_RES="$VALHEIM/BepInEx/e2e-client-results.txt"
HOST_OUT="${RR_E2E_HOST_LOG:-/tmp/valheim-e2e-host.log}"
CLI_OUT="${RR_E2E_CLI_LOG:-/tmp/valheim-e2e-client.log}"
HOST_E2E="/tmp/valheim-e2e-host-harness.log"
CLI_E2E="/tmp/valheim-e2e-client-harness.log"
PORT="${RR_E2E_PORT:-2456}"

rm -f "$HOST_RES" "$CLI_RES" "$HOST_OUT" "$CLI_OUT" "$HOST_E2E" "$CLI_E2E"
cd "$VALHEIM" || exit 99

export SteamAppId=892970 SteamGameId=892970
# Long window: revives, not expiry, must end every downed state.
export RR_E2E_WINDOW=90
# Which role goes down (the other revives); cycle count.
LOOP_DOWN="${RR_E2E_LOOP_DOWN:-host}"
LOOP_CYCLES="${RR_E2E_LOOP_CYCLES:-15}"
echo "=== reviveloop: down=$LOOP_DOWN cycles=$LOOP_CYCLES ==="
COMMON_ARGS="-screen-fullscreen 0 -screen-width 640 -screen-height 480"

echo "=== launching HOST ==="
env RR_E2E=1 RR_E2E_ROLE=host RR_E2E_SCENARIO=reviveloop RR_E2E_LOOP_DOWN="$LOOP_DOWN" RR_E2E_LOOP_CYCLES="$LOOP_CYCLES" RR_E2E_LOOP_MODE="${RR_E2E_LOOP_MODE:-revive}" \
    RR_E2E_PORT="$PORT" RR_E2E_WORLD="e2e_loop_$(date +%s)" \
    RR_E2E_RESULTS="$HOST_RES" RR_E2E_LOG="$HOST_E2E" \
    timeout 800 ./run_bepinex.sh ./valheim.x86_64 $COMMON_ARGS > "$HOST_OUT" 2>&1 &
HOST_PID=$!

i=0
until grep -q "opened CustomSocket" "$HOST_E2E" 2>/dev/null || ! kill -0 "$HOST_PID" 2>/dev/null; do
    i=$((i + 1))
    [ "$i" -gt 150 ] && { echo "!! host did not open socket within ${i}s"; break; }
    sleep 1
done
echo "=== host socket opened (waited ${i}s); launching CLIENT ==="

env RR_E2E=1 RR_E2E_ROLE=client RR_E2E_SCENARIO=reviveloop RR_E2E_LOOP_DOWN="$LOOP_DOWN" RR_E2E_LOOP_CYCLES="$LOOP_CYCLES" RR_E2E_LOOP_MODE="${RR_E2E_LOOP_MODE:-revive}" \
    RR_E2E_PORT="$PORT" RR_E2E_HOST=127.0.0.1 RR_E2E_WORLD="e2e_loop_$(date +%s)" \
    RR_E2E_RESULTS="$CLI_RES" RR_E2E_LOG="$CLI_E2E" \
    timeout 780 ./run_bepinex.sh ./valheim.x86_64 $COMMON_ARGS > "$CLI_OUT" 2>&1 &
CLI_PID=$!

wait "$HOST_PID"; echo "=== host exit: $? ==="
wait "$CLI_PID"; echo "=== client exit: $? ==="

echo "=== HOST RESULTS ==="; cat "$HOST_RES" 2>/dev/null || echo "(no host results)"
echo "=== CLIENT RESULTS ==="; cat "$CLI_RES" 2>/dev/null || echo "(no client results)"
echo "=== LEAK LINES (host) ==="; grep "E2E-LEAK" "$HOST_E2E" 2>/dev/null || echo "(none)"
echo "=== LEAK LINES (client) ==="; grep "E2E-LEAK" "$CLI_E2E" 2>/dev/null || echo "(none)"

#!/bin/sh
# Autonomous multiplayer E2E for Automations: launches two Valheim+BepInEx processes
# that connect over the patched CustomSocket (raw TCP) backend on localhost. The host
# builds and runs the factory; the client joins and verifies cross-client behaviour.
#
# Scenario is chosen with AUTO_E2E_SCENARIO (default "flow"):
#   flow   -- client claims the collector chest; asserts Bronze flows host->client.
#   wiring -- client asserts the host's pipe graph and buffers replicate over the wire.
set -u

VALHEIM="$HOME/.local/share/Steam/steamapps/common/Valheim"
SCENARIO="${AUTO_E2E_SCENARIO:-flow}"
HOST_RES="$VALHEIM/BepInEx/auto-e2e-host-results.txt"
CLI_RES="$VALHEIM/BepInEx/auto-e2e-client-results.txt"
HOST_OUT="${AUTO_E2E_HOST_LOG:-/tmp/automations-e2e-host.log}"
CLI_OUT="${AUTO_E2E_CLI_LOG:-/tmp/automations-e2e-client.log}"
HOST_E2E="/tmp/automations-e2e-host-harness.log"
CLI_E2E="/tmp/automations-e2e-client-harness.log"
PORT="${AUTO_E2E_PORT:-2456}"

rm -f "$HOST_RES" "$CLI_RES" "$HOST_OUT" "$CLI_OUT" "$HOST_E2E" "$CLI_E2E"
cd "$VALHEIM" || exit 99

export SteamAppId=892970 SteamGameId=892970
COMMON_ARGS="-screen-fullscreen 0 -screen-width 640 -screen-height 480"

echo "=== launching HOST (scenario=$SCENARIO) ==="
env AUTO_E2E=1 AUTO_E2E_ROLE=host AUTO_E2E_SCENARIO="$SCENARIO" AUTO_E2E_PORT="$PORT" AUTO_E2E_WORLD=auto_mp \
    AUTO_E2E_RESULTS="$HOST_RES" AUTO_E2E_LOG="$HOST_E2E" \
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

env AUTO_E2E=1 AUTO_E2E_ROLE=client AUTO_E2E_SCENARIO="$SCENARIO" AUTO_E2E_PORT="$PORT" AUTO_E2E_HOST=127.0.0.1 AUTO_E2E_WORLD=auto_mp \
    AUTO_E2E_RESULTS="$CLI_RES" AUTO_E2E_LOG="$CLI_E2E" \
    timeout 320 ./run_bepinex.sh ./valheim.x86_64 $COMMON_ARGS > "$CLI_OUT" 2>&1 &
CLI_PID=$!

wait "$HOST_PID"; echo "=== host exit: $? ==="
wait "$CLI_PID"; echo "=== client exit: $? ==="

echo "=== HOST RESULTS ==="; cat "$HOST_RES" 2>/dev/null || echo "(no host results)"
echo "=== CLIENT RESULTS ==="; cat "$CLI_RES" 2>/dev/null || echo "(no client results)"

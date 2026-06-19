#!/bin/sh
# Autonomous E2E launcher for RevivalRevived.
# Launches Valheim+BepInEx with the in-game test harness enabled, waits for it
# to finish (the harness quits the game itself), and prints the result file.
set -u

VALHEIM="$HOME/.local/share/Steam/steamapps/common/Valheim"
RESULTS="$VALHEIM/BepInEx/e2e-results.txt"
GAME_LOG="$VALHEIM/BepInEx/LogOutput.log"
RUN_LOG="${RR_E2E_RUNLOG:-/tmp/valheim-e2e-stdout.log}"

rm -f "$RESULTS" "$RUN_LOG"
: > "$GAME_LOG" 2>/dev/null || true

cd "$VALHEIM" || exit 99

export RR_E2E=1
export RR_E2E_RESULTS="$RESULTS"
# Make sure the Steam app id is visible to the Steamworks API when launched
# outside the Steam UI.
export SteamAppId=892970
export SteamGameId=892970

# Run windowed and small to keep GPU/compositor load low.
timeout 400 ./run_bepinex.sh ./valheim.x86_64 \
    -screen-fullscreen 0 -screen-width 800 -screen-height 600 \
    > "$RUN_LOG" 2>&1
echo "=== exit code: $? ==="
echo "=== RESULTS ($RESULTS) ==="
cat "$RESULTS" 2>/dev/null || echo "(no results file written)"

#!/bin/sh
# Autonomous single-process E2E for Automations: launches Valheim+BepInEx with the
# in-game harness, builds the full factory locally, asserts Bronze flows end to end,
# then quits and prints the result file.
set -u

VALHEIM="$HOME/.local/share/Steam/steamapps/common/Valheim"
RESULTS="$VALHEIM/BepInEx/auto-e2e-results.txt"
RUN_LOG="${AUTO_E2E_RUNLOG:-/tmp/automations-e2e-stdout.log}"

rm -f "$RESULTS" "$RUN_LOG"
cd "$VALHEIM" || exit 99

export AUTO_E2E=1
export AUTO_E2E_RESULTS="$RESULTS"
export AUTO_E2E_LOG="/tmp/automations-e2e-harness.log"
export SteamAppId=892970 SteamGameId=892970

timeout 400 ./run_bepinex.sh ./valheim.x86_64 \
    -screen-fullscreen 0 -screen-width 800 -screen-height 600 \
    > "$RUN_LOG" 2>&1
echo "=== exit code: $? ==="
echo "=== RESULTS ($RESULTS) ==="
cat "$RESULTS" 2>/dev/null || echo "(no results file written)"

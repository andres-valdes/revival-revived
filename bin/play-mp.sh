#!/bin/sh
# Launch two Valheim instances joined to the same world for manual testing of
# RevivalRevived. Instance 1 hosts over the raw-TCP CustomSocket backend;
# instance 2 auto-joins it on localhost. No tests run, nothing auto-quits --
# both windows are yours to play (down one character, revive with the other).
#
#   ./bin/play-mp.sh              # world "coop_world", 1600x900 windows
#   RR_E2E_WORLD=mytest ./bin/play-mp.sh
#
# Close both game windows (or Ctrl-C here) to stop.
set -u

VALHEIM="$HOME/.local/share/Steam/steamapps/common/Valheim"
PORT="${RR_E2E_PORT:-2456}"
WORLD="${RR_E2E_WORLD:-coop_world}"
W="${RR_PLAY_WIDTH:-1600}"
H="${RR_PLAY_HEIGHT:-900}"
HOST_LOG=/tmp/valheim-play-host.log
CLI_LOG=/tmp/valheim-play-client.log
HOST_HLOG=/tmp/valheim-play-host-harness.log
CLI_HLOG=/tmp/valheim-play-client-harness.log

rm -f "$HOST_LOG" "$CLI_LOG" "$HOST_HLOG" "$CLI_HLOG"
cd "$VALHEIM" || exit 99

export SteamAppId=892970 SteamGameId=892970
export RR_E2E=1 RR_E2E_MANUAL=1 RR_E2E_PORT="$PORT" RR_E2E_WORLD="$WORLD"

echo ">> starting HOST (world '$WORLD', port $PORT)..."
env RR_E2E_ROLE=host RR_E2E_PROFILE=coop_p1 RR_E2E_CHARNAME=PlayerOne RR_E2E_LOG="$HOST_HLOG" \
    ./run_bepinex.sh ./valheim.x86_64 -screen-fullscreen 0 -screen-width "$W" -screen-height "$H" \
    > "$HOST_LOG" 2>&1 &
HOST_PID=$!

trap 'echo; echo ">> stopping both instances"; kill $HOST_PID $CLI_PID 2>/dev/null; exit 0' INT TERM

i=0
until grep -q "opened CustomSocket" "$HOST_HLOG" 2>/dev/null; do
    i=$((i+1))
    [ "$i" -gt 180 ] && { echo "!! host did not open its socket within ${i}s (see $HOST_LOG)"; kill $HOST_PID 2>/dev/null; exit 1; }
    kill -0 "$HOST_PID" 2>/dev/null || { echo "!! host exited early (see $HOST_LOG)"; exit 1; }
    sleep 1
done
echo ">> host is up (took ${i}s); starting CLIENT..."

env RR_E2E_ROLE=client RR_E2E_PROFILE=coop_p2 RR_E2E_CHARNAME=PlayerTwo RR_E2E_HOST=127.0.0.1 RR_E2E_LOG="$CLI_HLOG" \
    ./run_bepinex.sh ./valheim.x86_64 -screen-fullscreen 0 -screen-width "$W" -screen-height "$H" \
    > "$CLI_LOG" 2>&1 &
CLI_PID=$!

echo ">> both instances launching. PlayerOne hosts, PlayerTwo auto-joins."
echo ">> tip: damage one character to 0 HP (e.g. fall damage) and revive with the other (hold E on the green grave)."
echo ">> logs: $HOST_HLOG / $CLI_HLOG   Ctrl-C here stops both."

wait "$HOST_PID" "$CLI_PID" 2>/dev/null
echo ">> both instances exited."

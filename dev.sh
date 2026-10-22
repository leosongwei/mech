#!/usr/bin/env bash
#
# MECH development helper.
#
#   ./dev.sh start     start the game server inside a byobu/tmux session
#   ./dev.sh attach    attach to that session (detach with F6 / Ctrl-a d)
#   ./dev.sh stop      kill the session
#   ./dev.sh status    list sessions
#   ./dev.sh build     build the web client into backend/assets
#   ./dev.sh dev       build once, then start the server
#
# The server is started exactly like the rest of this box's long running
# processes: a detached byobu session that can be re-attached later.
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SESSION="${MECH_SESSION:-mech}"
PORT="${MECH_PORT:-8010}"
PY="$ROOT/.venv/bin/python"
LOG="${MECH_LOG:-/tmp/mech-server.log}"

# byobu is a tmux wrapper; fall back to plain tmux when the byobu config dir is
# not writable (e.g. inside a sandbox) so the helper always works.
mux() {
  local cfg="${BYOBU_CONFIG_DIR:-$HOME/.byobu}"
  if command -v byobu-tmux >/dev/null 2>&1 && [ -w "$cfg" ]; then
    byobu-tmux -u "$@"
  else
    tmux -u "$@"
  fi
}

cmd_start() {
  command -v "$PY" >/dev/null 2>&1 || { echo "missing venv: run  uv venv && uv pip install -r backend/pyproject.toml"; exit 1; }
  if mux has-session -t "$SESSION" 2>/dev/null; then
    echo "session '$SESSION' already running - use ./dev.sh attach"
    return 0
  fi
  mux new-session -A -d -s "$SESSION" \
    bash -ic "cd '$ROOT/backend' && MECH_DEBUG=1 exec '$PY' run.py --port '$PORT' 2>&1 | tee '$LOG'"
  sleep 2
  echo "MECH server starting in byobu session '$SESSION' on http://127.0.0.1:$PORT"
  echo "  attach: ./dev.sh attach      logs: tail -f $LOG"
}

cmd_attach() {
  mux attach -t "$SESSION"
}

cmd_stop() {
  mux kill-session -t "$SESSION" 2>/dev/null && echo "session '$SESSION' stopped" || echo "session '$SESSION' not running"
}

cmd_status() {
  mux ls 2>/dev/null || echo "no sessions"
}

cmd_build() {
  ( cd "$ROOT/frontend" && npm install --cache /tmp/npmcache --no-audit --no-fund && npm run build )
  echo "client built into backend/assets"
}

case "${1:-start}" in
  start) cmd_start ;;
  attach) cmd_attach ;;
  stop) cmd_stop ;;
  status) cmd_status ;;
  build) cmd_build ;;
  dev) cmd_build; cmd_start ;;
  *) echo "usage: $0 {start|attach|stop|status|build|dev}"; exit 1 ;;
esac

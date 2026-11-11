#!/usr/bin/env bash
# The test suite: the server's tests, then the client's, then the comparison that
# says the two implementations of the fire control are the same function.
#
#   ./run_tests.sh              # all three, stop at the first failure
#   ./run_tests.sh cs           # the server's tests only
#   ./run_tests.sh js           # the client's tests only
#   ./run_tests.sh compare      # the cross-side comparison only
#   ./run_tests.sh --filter Fcs # a substring of the test name (both sides)
#
# Nothing here starts the server, installs a package or touches `assets/`: the tests
# are pure functions of the case table in `tests/cases/`, and the two runners are
# `node --test` (built into Node) and a console program (`tests/backend`).
#
# Output: each side writes its solutions table to $TMPDIR/mech-fcs-<side>.json, and
# `compare` reads those two files - so "run each side here, compare somewhere else"
# works too.
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
cd "$root"

mode=all
filter=""
while [ $# -gt 0 ]; do
  case "$1" in
    cs|backend)   mode=cs ;;
    js|frontend)  mode=js ;;
    compare)      mode=compare ;;
    all)          mode=all ;;
    --filter)     filter=${2:?--filter needs a value}; shift ;;
    --filter=*)   filter=${1#--filter=} ;;
    -h|--help)    sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "run_tests.sh: unknown argument '$1' (cs | js | compare | --filter NAME)" >&2
       exit 2 ;;
  esac
  shift
done

out=${TMPDIR:-/tmp}
# the sandbox's /tmp is a per-session directory that may not exist yet
mkdir -p "$out" 2>/dev/null || true
cs_json=$out/mech-fcs-cs.json
js_json=$out/mech-fcs-js.json

run_cs() {
  echo "== server (tests/backend)"
  local args=(--json "$cs_json")
  [ -n "$filter" ] && args+=(--filter "$filter")
  ./dotnet.sh run --project tests/backend -c Release -- "${args[@]}"
}

run_js() {
  echo "== client (tests/frontend)"
  if [ -n "$filter" ]; then
    node --test --test-name-pattern "$filter" tests/frontend/*/*.test.mjs
  else
    node --test tests/frontend/*/*.test.mjs
  fi
  node tests/frontend/fcs/emit.mjs "$js_json"
}

case "$mode" in
  cs)      run_cs ;;
  js)      run_js ;;
  compare) ;;
  all)     run_cs; run_js ;;
esac

if [ "$mode" = all ] || [ "$mode" = compare ]; then
  echo "== the two hosts agree (tests/compare.mjs)"
  node tests/compare.mjs "$cs_json" "$js_json"
fi

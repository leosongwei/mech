#!/usr/bin/env bash
# dotnet helper for this repo (build / run / test) - the server is `MechServer.csproj`
# in this directory, so `./dotnet.sh build` from anywhere works.
#
# dotnet writes first-run sentinels into $HOME and (on Linux) a mkdtemp under
# XDG_RUNTIME_DIR before it does anything else, and this sandbox can write
# neither - a bare `dotnet build` dies with EROFS before it reads the project.
# All of that state is kept in `.dotnet_tmp/` at the repo root instead: one
# directory, gitignored, and it survives between sessions unlike /tmp.
#
#   ./dotnet.sh build
#   ./dotnet.sh run -- --port 8010            (battle mode)
#   ./dotnet.sh run -- --port 8011 --range    (weapon range)
#
# Extra env is passed straight through (MECH_DEBUG=1, MECH_LOG_LEVEL=DEBUG, ...).
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# the sandbox already exports XDG_RUNTIME_DIR=/run/user/1000, which is read
# only - a `${VAR:-default}` would keep it, so these are set unconditionally
export DOTNET_CLI_HOME=$root/.dotnet_tmp/home
export NUGET_PACKAGES=$root/.dotnet_tmp/nuget
export XDG_RUNTIME_DIR=$root/.dotnet_tmp/run
export TMPDIR=$root/.dotnet_tmp/tmp
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES" "$XDG_RUNTIME_DIR" "$TMPDIR"
chmod 700 "$XDG_RUNTIME_DIR" 2>/dev/null || true
cd "$root"
exec dotnet "$@"

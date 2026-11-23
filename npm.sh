#!/usr/bin/env bash
# npm helper for this repo: install and build the client.
#
#   ./npm.sh install                 # dependencies into frontend/node_modules
#   ./npm.sh run build               # -> assets/ (deps + syntax + webpack)
#   ./npm.sh run check:deps          # the layer rule on its own
#
# Two things this script owns.
#
# 1. **npm's state lives in the project.** npm writes a cache, a log directory
#    and a tmp dir into $HOME ($HOME/.npm, $HOME/.npm/_logs), and this box's
#    $HOME is read only - a bare `npm install` dies with EROFS before it reads a
#    package.json. So the cache, the logs, the tmp dir and the HOME the build
#    runs under are all `frontend/.npm/` (gitignored), and `node_modules/` is
#    `frontend/node_modules/` - inside the project as well. A build leaves
#    nothing outside the working tree.
#
# 2. **The build runs sandboxed.** The whole filesystem is mounted read only
#    except this project, so a package's install script cannot touch anything
#    else. When the caller already provides that - an outer bubblewrap, or a
#    sandbox whose $HOME is read only - the run is not wrapped twice: the check
#    is `$HOME` writability, plus `MECH_BWRAP=1`, which this script sets for the
#    run it wraps. `MECH_NO_BWRAP=1` skips the wrap.
#
# Arguments go to npm unchanged, so `./npm.sh run build` is `npm run build` in
# `frontend/` with all of the above applied.
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
self="$root/$(basename "${BASH_SOURCE[0]}")"
state="$root/frontend/.npm"
orig_home="${HOME:-/}"

export npm_config_cache="$state/cache"
export npm_config_logs_dir="$state/logs"
export npm_config_tmp="$state/tmp"
export npm_config_userconfig="$state/npmrc"
# the build is not the place for the registry's marketing or a security audit
export npm_config_update_notifier=false
export npm_config_fund=false
export npm_config_audit=false
export HOME="$state/home"
export TMPDIR="$state/tmp"
export XDG_RUNTIME_DIR="$state/run"
mkdir -p "$npm_config_cache" "$npm_config_logs_dir" "$npm_config_tmp" "$HOME" "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR" 2>/dev/null || true

# Is the caller's environment already the protection this script would add?
# `MECH_BWRAP` is this script's own marker (it cannot be inherited, because the
# environment a wrapped run sees is the one below); otherwise a read-only $HOME
# means nothing under it can be written anyway.
protected_env() {
  [ "${MECH_BWRAP:-0}" = 1 ] && return 0
  [ -w "$orig_home" ] || return 0
  return 1
}

# bubblewrap needs unprivileged namespaces; where the kernel forbids them the
# build still runs, just without the box
sandbox_available() {
  command -v bwrap >/dev/null 2>&1 &&
    bwrap --ro-bind / / --dev-bind /dev /dev --proc /proc -- /bin/true >/dev/null 2>&1
}

if [ "${MECH_NO_BWRAP:-0}" != 1 ] && ! protected_env; then
  if sandbox_available; then
    # read-only root, the project writable, a fresh /tmp, the host's /dev, and
    # the whole environment (npm's state above) passed through
    exec bwrap \
      --ro-bind / / \
      --dev-bind /dev /dev \
      --proc /proc \
      --tmpfs /tmp \
      --bind "$root" "$root" \
      --chdir "$root" \
      --unshare-pid \
      --die-with-parent \
      --setenv MECH_BWRAP 1 \
      -- "$self" "$@"
  fi
  echo "npm.sh: bubblewrap unavailable - running the build without the sandbox" >&2
fi

cd "$root/frontend"
exec npm "$@"

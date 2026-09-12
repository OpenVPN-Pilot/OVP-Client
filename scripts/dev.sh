#!/bin/bash
#
# Builds and runs the application from the source tree. The macOS counterpart of scripts/dev.ps1, and
# it does the same three things in the same order.
#
# Three things have to happen in order, and doing them by hand is how an old copy ends up being the
# one that is running: stop whatever is open, build, start what was just built. Only one copy runs per
# user, so starting a new one while an old one is open hands the request to the old one and changes
# nothing on screen.
#
# This does not publish and does not build an installer. Use installer/build-macos.sh for that.
#
# Usage: scripts/dev.sh [--headless] [--connect <name>]... [--no-build] [--configuration Debug]

set -euo pipefail

repository=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
configuration='Debug'
headless='no'
build='yes'
connect=()

while [[ $# -gt 0 ]]; do
    case "$1" in
        --headless) headless='yes'; shift ;;
        --connect) connect+=("$2"); shift 2 ;;
        --no-build) build='no'; shift ;;
        --configuration) configuration="$2"; shift 2 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

application="${repository}/src/OpenVpnPilot.App/bin/${configuration}/net10.0/OpenVpnPilot"
command_line="${repository}/src/OpenVpnPilot.Cli/bin/${configuration}/net10.0/ovp"

# A build from the source tree is not self contained, so the host binary has to be told where .NET
# is. On Windows the installer registers that; on macOS an SDK unpacked into a home directory, which
# is what the official installer script does, is found through this variable and nowhere else.
# Without it the build starts, prints that .NET is not installed, and exits, which reads like the
# application failing rather than the shell missing a variable.
if [[ -z "${DOTNET_ROOT:-}" ]]; then
    if [[ -x "${HOME}/.dotnet/dotnet" ]]; then
        export DOTNET_ROOT="${HOME}/.dotnet"
    elif command -v dotnet > /dev/null; then
        export DOTNET_ROOT="$(dirname "$(readlink -f "$(command -v dotnet)" 2> /dev/null || command -v dotnet)")"
    fi
fi

# Whatever is open owns the profile store, and it is almost never the build about to be made. Matched
# by exact process name rather than by command line, so a terminal that merely mentions the path is
# not mistaken for the application.
if pgrep -x 'OpenVpnPilot' > /dev/null; then
    echo 'Stopping the copy that is running'

    [[ -x "$command_line" ]] && "$command_line" stop || true

    waited=0
    while pgrep -x 'OpenVpnPilot' > /dev/null && [[ $waited -lt 15 ]]; do
        sleep 1
        waited=$((waited + 1))
    done

    if pgrep -x 'OpenVpnPilot' > /dev/null; then
        echo 'The copy that is running did not stop. Close it and run this again.' >&2
        exit 1
    fi
fi

if [[ "$build" == 'yes' ]]; then
    echo "Building (${configuration})"
    dotnet build "${repository}/OpenVpnPilot.sln" --configuration "$configuration" --nologo
fi

[[ -x "$application" ]] \
    || { echo "Nothing to start: ${application} does not exist. Run without --no-build." >&2; exit 1; }

arguments=()
[[ "$headless" == 'yes' ]] && arguments+=('--headless')

for profile in ${connect[@]+"${connect[@]}"}; do
    arguments+=('--connect' "$profile")
done

echo "Starting ${application} ${arguments[*]-}"

# Detached from this terminal, so the prompt comes back rather than waiting for a window that is meant
# to outlive it. A build from the source tree is not a bundle, so it is started directly rather than
# through `open`: it gets no Dock entry and no menu bar of its own, which is what the bundle is for
# and what installer/build-macos.sh is for testing.
(
    cd "$(dirname "$application")"
    nohup "$application" ${arguments[@]+"${arguments[@]}"} > /dev/null 2>&1 &
)

echo ''
echo "The command is at ${command_line}"
echo 'Add its directory to PATH for this session with:'
echo "  export PATH=\"$(dirname "$command_line"):\$PATH\""

if [[ -n "${DOTNET_ROOT:-}" ]]; then
    echo "It needs .NET named as well:"
    echo "  export DOTNET_ROOT=\"${DOTNET_ROOT}\""
fi

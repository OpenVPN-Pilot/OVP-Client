# What a macOS build needs, checked before anything is built rather than discovered halfway through.
#
# Sourced by installer/build-macos.sh and installer/build-openvpn-macos.sh. It is not a script to run
# on its own and installs nothing: a build script that puts a toolchain on somebody's machine is doing
# something they did not ask for, and both of the things needed here have to be agreed to by the
# person installing them. It says what is missing and what to type.
#
# The catch this exists for: on macOS `cc`, `make`, `git`, `lipo`, `otool`, `strip` and `SetFile` all
# exist at /usr/bin on a machine where nothing is installed. They are stubs that open the developer
# tools dialog and fail, so `command -v` finds every one of them and the build still cannot run. The
# tools are therefore checked through xcode-select, which is what actually knows.

# Prints the block that explains one missing prerequisite.
preflight_advice() {
    case "$1" in
        developer-tools)
            printf '%s\n' \
                '  Xcode command line tools. Install them with:' \
                '' \
                '      xcode-select --install' \
                '' \
                '  A dialog appears and the download takes a few minutes. Xcode itself is not needed.'
            ;;
        dotnet)
            printf '%s\n' \
                '  The .NET 10 SDK, from Microsoft rather than from a package manager. Install it with:' \
                '' \
                '      curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh' \
                '      bash dotnet-install.sh --channel 10.0' \
                '' \
                '  That puts it in ~/.dotnet, which is where this build looks first. A source build from' \
                '  a package manager links libraries owned by the account that installed that manager,' \
                '  and one of the two things this produces runs as root.'
            ;;
        *)
            printf '  %s\n' "$1"
            ;;
    esac
}

# Checks everything named and reports all of it at once, because finding out about the second thing
# after installing the first is two waits instead of one.
#
# Usage: preflight_require developer-tools dotnet
preflight_require() {
    local missing=() requirement

    for requirement in "$@"; do
        case "$requirement" in
            developer-tools)
                if ! xcode-select --print-path > /dev/null 2>&1 \
                    || ! xcrun --find clang > /dev/null 2>&1; then
                    missing+=('developer-tools')
                fi
                ;;
            dotnet)
                if [[ ! -x "${HOME}/.dotnet/dotnet" ]] && ! command -v dotnet > /dev/null 2>&1; then
                    missing+=('dotnet')
                fi
                ;;
            *)
                command -v "$requirement" > /dev/null 2>&1 || missing+=("$requirement")
                ;;
        esac
    done

    [[ ${#missing[@]} -eq 0 ]] && return 0

    {
        if [[ ${#missing[@]} -eq 1 ]]; then
            printf '\n%s\n\n' 'One thing a build needs is missing from this machine.'
        else
            printf '\n%s\n\n' "${#missing[@]} things a build needs are missing from this machine."
        fi

        for requirement in "${missing[@]}"; do
            preflight_advice "$requirement"
            printf '\n'
        done

        printf '%s\n' 'Nothing has been installed for you. Run this again once they are in place.'
    } >&2

    exit 1
}

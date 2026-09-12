#!/bin/bash
#
# Builds the OpenVPN that the helper package installs and runs as root.
#
# The build exists because of two requirements that no packaged OpenVPN meets.
#
# The first is the name server hook. OpenVPN runs a command to apply pushed name servers, and where
# that command comes from decides what a configuration can make root do: a command named by
# --dns-updown is a user script and needs --script-security 2, while the one compiled in as the
# default runs at level 1 (dns.c calls openvpn_run_script for the first and openvpn_execve_check for
# the second). The helper forces level 1, so pushed name servers work only if the hook is baked into
# the binary. That is what SCRIPTDIR does here, and it points at the helper.
#
# The second is what the binary links. A build from a package manager loads its libraries from a
# directory owned by the account that installed it, and running that as root hands root to anything
# running as that account. Everything here is linked statically from pinned sources, and the result
# is checked to use nothing but the system libraries.
#
# The sources are the ones the projects publish, pinned by checksum. The checksums are the ones
# Homebrew's formulae carry for the same releases, which is a second party having looked at them.
#
# OpenVPN is GPLv2. The package therefore ships the licence, this script and a record of exactly
# which sources went in, so that what is distributed can be built again from what is named.
#
# Usage: installer/build-openvpn-macos.sh [--architectures "arm64 x86_64"] [--jobs N] [--force]

set -euo pipefail

readonly OPENVPN_VERSION='2.7.7'
readonly OPENVPN_URL="https://swupdate.openvpn.net/community/releases/openvpn-${OPENVPN_VERSION}.tar.gz"
readonly OPENVPN_SHA256='3ab8f48fd6c26d49ba2333a092433949afdb5c85c0e6a1ff265784fbc04a2463'

readonly OPENSSL_VERSION='3.6.4'
readonly OPENSSL_URL="https://github.com/openssl/openssl/releases/download/openssl-${OPENSSL_VERSION}/openssl-${OPENSSL_VERSION}.tar.gz"
readonly OPENSSL_SHA256='9bffaa1ad1e07b354c21bd3324ec02fa15579f45a7d0494b3e74bc449b7333ef'

readonly LZO_VERSION='2.10'
readonly LZO_URL="https://www.oberhumer.com/opensource/lzo/download/lzo-${LZO_VERSION}.tar.gz"
readonly LZO_SHA256='c0f892943208266f9b6543b3ae308fab6284c5c90e627931446fb49b4221a072'

readonly LZ4_VERSION='1.10.0'
readonly LZ4_URL="https://github.com/lz4/lz4/archive/refs/tags/v${LZ4_VERSION}.tar.gz"
readonly LZ4_SHA256='537512904744b35e232912055ccf8ec66d768639ff3abe5788d90d792ec5f48b'

# The oldest macOS the result has to run on. The same floor as the application.
readonly DEPLOYMENT_TARGET='13.0'

# Where the compiled in name server hook lives. A directory of our own under the one Apple reserves
# for privileged helpers: owned by root, free of spaces because the path goes through a Makefile and
# a compiler command line, and not shared with anything else that might install a file of that name.
readonly SCRIPT_DIRECTORY='/Library/PrivilegedHelperTools/openvpnpilot'

repository=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
work="${repository}/artifacts/openvpn"
architectures='arm64 x86_64'
jobs=$(sysctl -n hw.ncpu)
force='no'

while [[ $# -gt 0 ]]; do
    case "$1" in
        --architectures) architectures="$2"; shift 2 ;;
        --jobs) jobs="$2"; shift 2 ;;
        --force) force='yes'; shift ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

say() { printf '%s\n' "$*"; }
fail() { printf '%s\n' "$*" >&2; exit 1; }

# A source is downloaded once and then only ever verified. A checksum that does not match is the end
# of the build: it means the file is not the release this script was written against.
fetch() {
    local url="$1" expected="$2" file="${work}/src/$3"

    if [[ -f "$file" ]]; then
        local have
        have=$(shasum -a 256 "$file" | cut -d' ' -f1)

        if [[ "$have" == "$expected" ]]; then
            say "  have $(basename "$file")"
            return 0
        fi

        say "  $(basename "$file") does not match its checksum and is fetched again"
        rm -f "$file"
    fi

    say "  fetching $(basename "$file")"
    curl --fail --location --silent --show-error --output "$file" "$url"

    local got
    got=$(shasum -a 256 "$file" | cut -d' ' -f1)

    if [[ "$got" != "$expected" ]]; then
        rm -f "$file"
        fail "The checksum of $(basename "$file") is $got and not $expected. Nothing is built from it."
    fi
}

unpack() {
    local tarball="${work}/src/$1" into="$2"

    rm -rf "$into"
    mkdir -p "$into"
    tar -xzf "$tarball" -C "$into" --strip-components=1
}

# The triplet older autotools know for this architecture. The bundled config.sub of lzo predates
# Apple silicon and rejects arm64-apple-darwin, whose canonical spelling is aarch64.
host_triplet() {
    case "$1" in
        arm64|aarch64) echo 'aarch64-apple-darwin' ;;
        x86_64) echo 'x86_64-apple-darwin' ;;
        *) fail "No host triplet is known for $1." ;;
    esac
}

build_for() {
    local architecture="$1"
    local prefix="${work}/deps/${architecture}"
    local tree="${work}/build/${architecture}"

    say ""
    say "Building for ${architecture}"

    export MACOSX_DEPLOYMENT_TARGET="$DEPLOYMENT_TARGET"
    export GIT_CEILING_DIRECTORIES="$work"

    local flags="-arch ${architecture} -mmacosx-version-min=${DEPLOYMENT_TARGET} -O2"

    # Cross compilation is declared only when it is one. Naming a host for the architecture the
    # machine already has puts configure into cross mode, where it stops running the test programs
    # it would otherwise use to check what it is building for.
    local cross=()

    if [[ "$architecture" != "$(uname -m)" ]]; then
        cross=(--host="$(host_triplet "$architecture")")
    fi

    mkdir -p "$prefix"

    if [[ -f "${prefix}/lib/libcrypto.a" ]]; then
        say "  openssl ${OPENSSL_VERSION} is already built"
    else
        say "  openssl ${OPENSSL_VERSION}"
        unpack "openssl-${OPENSSL_VERSION}.tar.gz" "${tree}/openssl"
        (
            cd "${tree}/openssl"
            local target='darwin64-arm64-cc'
            [[ "$architecture" == 'x86_64' ]] && target='darwin64-x86_64-cc'

            ./Configure "$target" \
                --prefix="$prefix" \
                --openssldir=/private/etc/ssl \
                no-shared no-module no-tests no-docs no-legacy \
                "-mmacosx-version-min=${DEPLOYMENT_TARGET}" > "${tree}/openssl-configure.log"

            make -j"$jobs" build_libs > "${tree}/openssl-make.log" 2>&1
            make install_dev > "${tree}/openssl-install.log" 2>&1
        )
    fi

    if [[ -f "${prefix}/lib/liblzo2.a" ]]; then
        say "  lzo ${LZO_VERSION} is already built"
    else
        say "  lzo ${LZO_VERSION}"
        unpack "lzo-${LZO_VERSION}.tar.gz" "${tree}/lzo"
        (
            cd "${tree}/lzo"
            ./configure --prefix="$prefix" --enable-static --disable-shared \
                "${cross[@]+"${cross[@]}"}" \
                CFLAGS="$flags" > "${tree}/lzo-configure.log"

            make -j"$jobs" > "${tree}/lzo-make.log" 2>&1
            make install > "${tree}/lzo-install.log" 2>&1
        )
    fi

    if [[ -f "${prefix}/lib/liblz4.a" ]]; then
        say "  lz4 ${LZ4_VERSION} is already built"
    else
        say "  lz4 ${LZ4_VERSION}"
        unpack "lz4-${LZ4_VERSION}.tar.gz" "${tree}/lz4"
        (
            cd "${tree}/lz4"
            make -j"$jobs" liblz4.a CFLAGS="$flags" BUILD_SHARED=no > "${tree}/lz4-make.log" 2>&1
            mkdir -p "${prefix}/include" "${prefix}/lib"
            cp lib/lz4.h lib/lz4hc.h lib/lz4frame.h "${prefix}/include/"
            cp lib/liblz4.a "${prefix}/lib/"
        )
    fi

    say "  openvpn ${OPENVPN_VERSION}"
    unpack "openvpn-${OPENVPN_VERSION}.tar.gz" "${tree}/openvpn"
    (
        cd "${tree}/openvpn"

        # Everything that could run code as root is left out: no plug-in support at all, and no
        # pkcs11. What stays is what a client needs, including both compression libraries, because a
        # server that pushes compression has to be recognised rather than fail as a mystery.
        #
        # The source is unpacked inside this repository's working tree, and configure asks git for the
        # revision of whatever tree it finds and stamps it into the version string, which would make
        # the binary report a commit of this project as the origin of OpenVPN. GIT= does not stop it,
        # because configure looks the program up again; a ceiling does, because it is what tells git
        # not to search above a directory.
        ./configure \
            --prefix="${tree}/stage" \
            "${cross[@]+"${cross[@]}"}" \
            --disable-plugins \
            --disable-plugin-auth-pam \
            --disable-plugin-down-root \
            --disable-debug \
            --enable-lzo \
            --enable-lz4 \
            SCRIPTDIR="$SCRIPT_DIRECTORY" \
            CFLAGS="$flags -I${prefix}/include" \
            LDFLAGS="-arch ${architecture} -L${prefix}/lib" \
            OPENSSL_CFLAGS="-I${prefix}/include" \
            OPENSSL_LIBS="${prefix}/lib/libssl.a ${prefix}/lib/libcrypto.a" \
            LZO_CFLAGS="-I${prefix}/include" \
            LZO_LIBS="${prefix}/lib/liblzo2.a" \
            LZ4_CFLAGS="-I${prefix}/include" \
            LZ4_LIBS="${prefix}/lib/liblz4.a" > "${tree}/openvpn-configure.log"

        make -j"$jobs" > "${tree}/openvpn-make.log" 2>&1
    )

    local built="${tree}/openvpn/src/openvpn/openvpn"
    [[ -x "$built" ]] || fail "The build for ${architecture} produced no openvpn."

    mkdir -p "${work}/stage"
    cp "$built" "${work}/stage/openvpn-${architecture}"
}

# What the result links has to be nothing but the system. A library from anywhere else would be a
# library someone other than root can replace, and this binary runs as root.
check_libraries() {
    local binary="$1" foreign

    # Only the indented lines name a library. The others name the binary itself, once per
    # architecture, which is what a universal binary has and what a check on the first line misses.
    foreign=$(otool -L "$binary" | grep '^[[:space:]]' | awk '{print $1}' \
        | grep -v -E '^(/usr/lib/|/System/Library/)' || true)

    if [[ -n "$foreign" ]]; then
        fail "The build links libraries that are not part of the system:"$'\n'"$foreign"
    fi

    say "  links only system libraries"
}

mkdir -p "${work}/src" "${work}/stage"

if [[ "$force" == 'yes' ]]; then
    rm -rf "${work}/build" "${work}/deps" "${work}/stage"
    mkdir -p "${work}/stage"
fi

say "Sources"
# Named here rather than taken from the URL: what a release archive is called on the way down is not
# always what it is called upstream, and lz4's is simply the tag.
fetch "$OPENVPN_URL" "$OPENVPN_SHA256" "openvpn-${OPENVPN_VERSION}.tar.gz"
fetch "$OPENSSL_URL" "$OPENSSL_SHA256" "openssl-${OPENSSL_VERSION}.tar.gz"
fetch "$LZO_URL" "$LZO_SHA256" "lzo-${LZO_VERSION}.tar.gz"
fetch "$LZ4_URL" "$LZ4_SHA256" "lz4-${LZ4_VERSION}.tar.gz"

for architecture in $architectures; do
    build_for "$architecture"
done

say ""
say "Assembling"

result="${work}/stage/openvpn"
slices=()

for architecture in $architectures; do
    slices+=("${work}/stage/openvpn-${architecture}")
done

if [[ ${#slices[@]} -gt 1 ]]; then
    lipo -create "${slices[@]}" -output "$result"
    say "  universal: $(lipo -archs "$result")"
else
    cp "${slices[0]}" "$result"
    say "  single architecture: $(lipo -archs "$result")"
fi

strip -S "$result"
check_libraries "$result"

# The name server script OpenVPN ships. The helper runs it after it has written down what to undo,
# so it is installed as a file of its own rather than as the compiled in hook.
one_tree=$(set -- $architectures; echo "$1")
cp "${work}/build/${one_tree}/openvpn/distro/dns-scripts/dns-updown" "${work}/stage/dns-updown"
chmod 755 "${work}/stage/dns-updown"

cp "${work}/build/${one_tree}/openvpn/COPYING" "${work}/stage/openvpn-COPYING"
cp "${work}/build/${one_tree}/openvpn/COPYRIGHT.GPL" "${work}/stage/openvpn-COPYRIGHT.GPL"

# What went in, so that what comes out can be made again. Part of what is shipped.
{
    printf 'OpenVpnPilot ships this build of OpenVPN. It was made from these sources:\n\n'
    printf '  openvpn %s\n    %s\n    sha256 %s\n\n' "$OPENVPN_VERSION" "$OPENVPN_URL" "$OPENVPN_SHA256"
    printf '  openssl %s\n    %s\n    sha256 %s\n\n' "$OPENSSL_VERSION" "$OPENSSL_URL" "$OPENSSL_SHA256"
    printf '  lzo %s\n    %s\n    sha256 %s\n\n' "$LZO_VERSION" "$LZO_URL" "$LZO_SHA256"
    printf '  lz4 %s\n    %s\n    sha256 %s\n\n' "$LZ4_VERSION" "$LZ4_URL" "$LZ4_SHA256"
    printf 'Architectures: %s\n' "$(lipo -archs "$result")"
    printf 'Oldest macOS: %s\n' "$DEPLOYMENT_TARGET"
    printf 'Name server hook compiled in as: %s/dns-updown\n\n' "$SCRIPT_DIRECTORY"
    printf 'The script that built it is installer/build-openvpn-macos.sh in the OpenVpnPilot\n'
    printf 'repository, which carries the exact configure arguments.\n\n'
    printf 'OpenVPN is distributed under the GNU General Public License version 2; its licence is\n'
    printf 'beside this file as openvpn-COPYING.\n'
} > "${work}/stage/openvpn-provenance.txt"

say ""
say "Wrote ${result}"
"$result" --version | head -3 || true

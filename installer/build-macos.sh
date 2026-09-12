#!/bin/bash
#
# Builds the macOS installers: the application as a bundle in a disk image, and the privileged
# helper as a package.
#
# There are two of them because they are installed by different people at different moments. The
# application is dragged to Applications by whoever uses it and needs no password. The helper runs as
# root, so installing it asks for one, and until it is installed the application says so and offers
# the link rather than failing at the first connection.
#
# Neither can be notarised: that needs an Apple Developer ID, and there is none. The signature here
# is ad-hoc, which is enough for the system to run the code and not enough for Gatekeeper to let it
# through without being asked. What that looks like is in the README.
#
# Usage: installer/build-macos.sh [--configuration Release] [--version 1.2.3]
#                                 [--runtime osx-arm64|osx-x64] [--self-contained true|false]
#                                 [--skip-openvpn] [--app-only] [--helper-only]

set -euo pipefail

readonly BUNDLE_IDENTIFIER='org.openvpnpilot.app'

# What the bundle is called on disk. The Finder labels an application with its file name and with
# nothing else: CFBundleDisplayName, a localized InfoPlist.strings and LSHasLocalizedDisplayName were
# all tried and the Finder went on showing the file name, so a bundle that is to read as
# "OpenVPN Pilot" in the disk image, in Applications and in the Dock has to be called that. The
# compact form stays where a name has to be one word: the executable inside, the bundle identifier,
# the data directory and the command.
# It was OpenVpnPilot.app up to 1.3.0; the package scripts look for both so an installation made
# before the rename keeps working.
readonly BUNDLE_NAME='OpenVPN Pilot.app'
readonly HELPER_IDENTIFIER='org.openvpnpilot.helper'
readonly MINIMUM_MACOS='13.0'

# Where the installed parts go. The same paths the code names in HelperInstallation, and a test holds
# the compiled in name server hook and this script together.
readonly HELPER_TOOLS='/Library/PrivilegedHelperTools'
readonly SUPPORT_DIRECTORY='/Library/Application Support/OpenVpnPilot'
readonly COMMAND_LINK='/usr/local/bin/ovp'
readonly AUTHORISED_GROUP='openvpnpilot'

repository=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
configuration='Release'
version=''
runtime=''
self_contained='true'
skip_openvpn='no'
build_app='yes'
build_helper='yes'

while [[ $# -gt 0 ]]; do
    case "$1" in
        --configuration) configuration="$2"; shift 2 ;;
        --version) version="$2"; shift 2 ;;
        --runtime) runtime="$2"; shift 2 ;;
        --self-contained) self_contained="$2"; shift 2 ;;
        --skip-openvpn) skip_openvpn='yes'; shift ;;
        --app-only) build_helper='no'; shift ;;
        --helper-only) build_app='no'; shift ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

say() { printf '%s\n' "$*"; }
fail() { printf '%s\n' "$*" >&2; exit 1; }

[[ "$(uname -s)" == 'Darwin' ]] || fail 'This script builds macOS installers and has to run on macOS.'

# Checked before anything is built, and all of it at once. On a Mac where nothing has been installed
# the tools below are stubs that open a dialog and fail, so a build that starts anyway gets a long way
# in before saying something that does not name what is wrong.
# shellcheck source=installer/preflight.sh
source "${repository}/installer/preflight.sh"
preflight_require developer-tools dotnet

if [[ -z "$runtime" ]]; then
    runtime=$([[ "$(uname -m)" == 'arm64' ]] && echo 'osx-arm64' || echo 'osx-x64')
fi

case "$runtime" in
    osx-arm64|osx-x64) ;;
    *) fail "The runtime has to be osx-arm64 or osx-x64, not '$runtime'." ;;
esac

if [[ -z "$version" ]]; then
    version=$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "${repository}/Directory.Build.props" | head -1)
fi

[[ -n "$version" ]] || fail 'No version was given and none could be read from Directory.Build.props.'

publish="${repository}/artifacts/install"
output="${repository}/artifacts/release"
staging="${repository}/artifacts/macos"

# The .NET the release is built with has to be the one from Microsoft. A source build from a package
# manager links that manager's libraries, which are owned by the account that installed it, and this
# produces something that runs as root and something that is handed to other people.
dotnet="${HOME}/.dotnet/dotnet"

if [[ ! -x "$dotnet" ]]; then
    dotnet=$(command -v dotnet)
fi

say "Building OpenVpnPilot ${version} (${configuration}, ${runtime})"
say "  dotnet: ${dotnet}"

# A previous publish is cleared rather than published over, so a file that is no longer part of the
# application cannot end up inside the installer. Anything still running from there is named first:
# deleting it from under a running copy produces an error about one file and nothing useful.
clear_publish() {
    [[ -d "$publish" ]] || return 0

    local holders
    holders=$(pgrep -f "$publish" || true)

    if [[ -n "$holders" ]]; then
        fail "The publish directory is in use by process $(echo "$holders" | tr '\n' ' '). Close it and run this again, for example with: ovp stop"
    fi

    rm -rf "$publish"
}

# Signed with --deep, which walks the bundle from the inside out and signs every nested item before
# the bundle itself.
#
# Apple discourages --deep for a signature meant for distribution, in favour of signing each nested
# item on purpose. That advice does not fit here, and measuring says why: everything under
# Contents/MacOS counts as nested code, which for a .NET application is several hundred managed
# assemblies, and signing the bundle while any of them is unsigned is refused outright. codesign can
# sign them, as generic code rather than Mach-O, and --deep is what does that in one pass. The result
# carries the identifier from the Info.plist, binds that plist, and seals every file in the bundle.
sign_bundle() {
    local bundle="$1"

    codesign --force --deep --sign - --timestamp=none "$bundle" \
        || fail 'The bundle could not be signed.'

    codesign --verify --deep --strict "$bundle" || fail 'The bundle did not verify after signing.'

    # Said out loud, because it is the one thing about this build a person has to know.
    say '  signed ad-hoc; Gatekeeper will refuse it until the user allows it, which the README explains'
}

# What a binary links has to be nothing but the system, for the same reason the OpenVPN build is
# checked: a library from a directory another account can write to is that account's code.
check_libraries() {
    local binary="$1" foreign

    foreign=$(otool -L "$binary" | grep '^[[:space:]]' | awk '{print $1}' \
        | grep -v -E '^(/usr/lib/|/System/Library/|@rpath/|@executable_path/)' || true)

    if [[ -n "$foreign" ]]; then
        fail "$(basename "$binary") links libraries that are not part of the system:"$'\n'"$foreign"
    fi
}

build_application() {
    say ''
    say 'Application'

    clear_publish
    mkdir -p "$publish" "$output"

    for project in 'src/OpenVpnPilot.App' 'src/OpenVpnPilot.Cli'; do
        say "  publishing ${project}"

        "$dotnet" publish "${repository}/${project}" \
            --configuration "$configuration" \
            --runtime "$runtime" \
            --self-contained "$self_contained" \
            --output "$publish" \
            --nologo \
            -p:Version="$version" \
            > "${staging}/publish-$(basename "$project").log" \
            || { tail -20 "${staging}/publish-$(basename "$project").log"; fail "Publishing ${project} failed."; }
    done

    # A publish leaves the debugging symbols behind, which double the size of the package and are of
    # no use on a machine that only runs the application.
    find "$publish" -name '*.pdb' -delete
    find "$publish" -name '*.dsym' -prune -exec rm -rf {} + 2> /dev/null || true

    local app="${staging}/${BUNDLE_NAME}"
    rm -rf "$app"
    mkdir -p "${app}/Contents/MacOS" "${app}/Contents/Resources"

    cp -R "${publish}/." "${app}/Contents/MacOS/"

    [[ -f "${repository}/assets/artwork/OpenVpnPilot.icns" ]] \
        || fail 'assets/artwork/OpenVpnPilot.icns is missing. Run: swift assets/make-artwork.swift'

    cp "${repository}/assets/artwork/OpenVpnPilot.icns" "${app}/Contents/Resources/"

    write_information_plist "${app}/Contents/Info.plist"

    # Four bytes of type and creator, which the Finder still reads to know this is an application.
    printf 'APPL????' > "${app}/Contents/PkgInfo"

    chmod +x "${app}/Contents/MacOS/OpenVpnPilot" "${app}/Contents/MacOS/ovp"

    say '  signing'
    sign_bundle "$app"

    build_disk_image "$app"
}

# The window a disk image opens is the first thing anyone sees of this, and a window that opens as a
# plain list of two items reads as something half finished. What makes it look like every other
# application's is not decoration: a fixed window size, no toolbar, the two icons at fixed positions
# with a background drawn to match, and a volume icon.
#
# None of that lives in the disk image as data anyone can write directly. The Finder keeps it in a
# .DS_Store that only the Finder writes, so the image is built writable, mounted, arranged through
# the Finder, and only then compressed. This is the same sequence every tool that does this uses, for
# the same reason.
#
# Positions and the window size are the ones assets/make-artwork.swift drew the background for. Change
# one and the other has to change with it.
readonly WINDOW_WIDTH=640
readonly WINDOW_HEIGHT=400

# The bounds the Finder is given describe the whole window, title bar included, while the background
# fills only what is below it. Measured on macOS 26: a window asked for 400 shows 372 of the picture
# and cuts the rest off the bottom. The title bar is therefore added to what is asked for.
readonly TITLE_BAR_HEIGHT=28
readonly ICON_SIZE=128
readonly APPLICATION_POSITION_X=170
readonly APPLICATION_POSITION_Y=205
readonly APPLICATIONS_POSITION_X=470
readonly APPLICATIONS_POSITION_Y=205

build_disk_image() {
    local app="$1"
    local image="${output}/OpenVpnPilot-${version}-${runtime}.dmg"
    local volume="OpenVPN Pilot ${version}"
    local room="${staging}/image"
    local writable="${staging}/OpenVpnPilot-rw.dmg"
    local mounted="/Volumes/${volume}"

    [[ -f "${repository}/assets/artwork/dmg-background.png" ]] \
        || fail 'assets/artwork/dmg-background.png is missing. Run: swift assets/make-artwork.swift'

    rm -rf "$room"
    mkdir -p "${room}/.background"
    cp -R "$app" "${room}/"
    cp "${repository}/assets/artwork/dmg-background.png" "${room}/.background/background.png"

    # The link is the whole gesture: the window opens, the application is dragged onto it.
    ln -s /Applications "${room}/Applications"

    # Room for the payload and for what the Finder writes on top of it. A writable image that runs
    # out of space mid-arrangement fails in ways that are hard to read.
    local kilobytes
    kilobytes=$(du -sk "$room" | cut -f1)
    local megabytes=$(( kilobytes / 1024 + 60 ))

    # A volume of this name already mounted would push this one aside to a name with a number after
    # it, and everything below would then be done to the wrong disk.
    [[ -d "$mounted" ]] && hdiutil detach "$mounted" -quiet 2> /dev/null

    rm -f "$writable"
    hdiutil create \
        -volname "$volume" \
        -srcfolder "$room" \
        -fs HFS+ \
        -format UDRW \
        -size "${megabytes}m" \
        -quiet \
        "$writable" \
        || fail 'The writable disk image could not be created.'

    # Where it actually landed, read back rather than assumed. The name a volume is given and the
    # path it is mounted at are not the same thing whenever something else is already using that
    # name, and doing the rest of this to the wrong path is how an image ends up without the parts
    # that were copied onto something else.
    local attached
    attached=$(hdiutil attach "$writable" -readwrite -noverify -noautoopen -plist) \
        || fail 'The writable disk image could not be mounted.'

    mounted=$(printf '%s' "$attached" \
        | awk '/<key>mount-point<\/key>/ { getline; gsub(/.*<string>|<\/string>.*/, ""); print; exit }')

    [[ -n "$mounted" && -d "$mounted" ]] \
        || fail 'The writable disk image reported no mount point.'

    # The Finder is told which disk to arrange by name, so the name it actually got is read back too.
    volume=$(basename "$mounted")

    say "  mounted at ${mounted}"

    say '  arranging the window'
    arrange_window "$volume" || fail 'The Finder could not arrange the disk image window. It needs permission to be controlled by whatever runs this script, which macOS asks for once, under System Settings, Privacy & Security, Automation.'

    # Everything in the image belongs to whoever mounts it, so nothing carries a group or other write
    # bit out of this build.
    chmod -Rf go-w "$mounted" 2> /dev/null || true
    sync

    # The icon the disk itself shows, on the desktop, in the sidebar and in its own title bar. Two
    # things about it were measured rather than assumed.
    #
    # It goes on the mounted volume and not into the folder the image was made from: hdiutil does
    # something of its own with a .VolumeIcon.icns it finds there, and the file was not in the result.
    #
    # And it goes on after the Finder has finished, not before. Measured on macOS 26: opening the
    # window deletes the file and clears the attribute again, every time, so an icon set first is an
    # icon that is gone by the time the image is compressed.
    cp "${repository}/assets/artwork/OpenVpnPilot.icns" "${mounted}/.VolumeIcon.icns"
    SetFile -a C "$mounted" || fail 'The volume icon attribute could not be set.'

    # Checked rather than assumed, because both halves of it are quiet when they fail: a missing file
    # leaves the attribute pointing at nothing, and an attribute that was not set leaves the file
    # unread. Either way the disk shows the generic icon and nothing says why.
    [[ -f "${mounted}/.VolumeIcon.icns" ]] || fail 'The volume icon did not survive onto the image.'
    [[ "$(GetFileInfo -aC "$mounted")" == '1' ]] || fail 'The volume icon attribute did not stay set.'

    detach_volume "$mounted"

    rm -f "$image"
    hdiutil convert "$writable" -format UDZO -imagekey zlib-level=9 -o "$image" -quiet \
        || fail 'The disk image could not be compressed.'

    rm -f "$writable"

    say "  wrote ${image}"
}

# The Finder holds a volume it has open for a moment after it is told to close it, so the first
# detach can be refused by something that is about to let go anyway.
detach_volume() {
    local mounted="$1" attempt

    for attempt in 1 2 3 4 5; do
        if hdiutil detach "$mounted" -quiet 2> /dev/null; then
            return 0
        fi

        sleep 2
    done

    hdiutil detach "$mounted" -force -quiet 2> /dev/null \
        || fail "The disk image stayed mounted at ${mounted}."
}

# Told to the Finder rather than written into the image, because the Finder is the only thing that
# writes the file these settings live in.
#
# Two things this got wrong first. Every command is inside a longer timeout than the two minutes an
# AppleEvent is given by default: the Finder is not always quick to answer while it is opening a
# volume, and the default expiring leaves the window half arranged, which is worse than not arranged
# at all. And the waiting is outside the block that talks to the Finder, because `delay` inside it is
# a command the Finder is asked to carry out and counts against the same timeout.
arrange_window() {
    local volume="$1"

    osascript <<APPLESCRIPT
with timeout of 600 seconds
    tell application "Finder"
        tell disk "${volume}"
            open
        end tell
    end tell
end timeout

delay 1

with timeout of 600 seconds
    tell application "Finder"
        tell disk "${volume}"
            set current view of container window to icon view
            set toolbar visible of container window to false
            set statusbar visible of container window to false
            set the bounds of container window to {180, 140, ${WINDOW_WIDTH} + 180, ${WINDOW_HEIGHT} + ${TITLE_BAR_HEIGHT} + 140}

            set options to the icon view options of container window
            set arrangement of options to not arranged
            set icon size of options to ${ICON_SIZE}
            set text size of options to 13
            set background picture of options to file ".background:background.png"

            set position of item "${BUNDLE_NAME}" of container window to {${APPLICATION_POSITION_X}, ${APPLICATION_POSITION_Y}}
            set position of item "Applications" of container window to {${APPLICATIONS_POSITION_X}, ${APPLICATIONS_POSITION_Y}}

            update without registering applications
        end tell
    end tell
end timeout

delay 3

-- Closed and opened again, so what is written down is what the window reopens with rather than what
-- it happened to be showing while it was being arranged.
with timeout of 600 seconds
    tell application "Finder"
        tell disk "${volume}"
            close
            open
            update without registering applications
        end tell
    end tell
end timeout

delay 3

with timeout of 600 seconds
    tell application "Finder"
        tell disk "${volume}"
            close
        end tell
    end tell
end timeout
APPLESCRIPT
}

write_information_plist() {
    cat > "$1" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key>
    <string>${BUNDLE_IDENTIFIER}</string>
    <!--
        The spaced form, because this is the name the system shows people: under the icon in the
        Finder, above a notification, and in the menu bar. It is the same name Windows is given
        through SetCurrentProcessExplicitAppUserModelID, so one application is called one thing on
        both. The compact form stays where a name has to be one word, which is the bundle on disk,
        the bundle identifier and the data directory.
    -->
    <key>CFBundleName</key>
    <string>OpenVPN Pilot</string>
    <key>CFBundleDisplayName</key>
    <string>OpenVPN Pilot</string>
    <key>CFBundleExecutable</key>
    <string>OpenVpnPilot</string>
    <key>CFBundleIconFile</key>
    <string>OpenVpnPilot</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>${version}</string>
    <key>CFBundleVersion</key>
    <string>${version}</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>${MINIMUM_MACOS}</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.utilities</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSSupportsAutomaticGraphicsSwitching</key>
    <true/>
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key>
            <string>OpenVPN configuration</string>
            <key>CFBundleTypeExtensions</key>
            <array>
                <string>ovpn</string>
            </array>
            <key>CFBundleTypeIconFile</key>
            <string>OpenVpnPilot</string>
            <!--
                Alternate rather than Owner: the OpenVPN GUI is usually what owns this type, and
                taking it would change what a double click does for everything already installed.
                The application appears in Open with, which is what is wanted.
            -->
            <key>CFBundleTypeRole</key>
            <string>Viewer</string>
            <key>LSHandlerRank</key>
            <string>Alternate</string>
        </dict>
        <dict>
            <key>CFBundleTypeName</key>
            <string>OpenVpnPilot package</string>
            <key>CFBundleTypeExtensions</key>
            <array>
                <string>ovppkg</string>
            </array>
            <key>CFBundleTypeIconFile</key>
            <string>OpenVpnPilot</string>
            <key>CFBundleTypeRole</key>
            <string>Editor</string>
            <!-- Its own format, so this application owns it. -->
            <key>LSHandlerRank</key>
            <string>Owner</string>
        </dict>
    </array>
</dict>
</plist>
PLIST
}

build_helper_package() {
    say ''
    say 'Helper'

    local openvpn="${repository}/artifacts/openvpn/stage"

    if [[ "$skip_openvpn" == 'no' ]]; then
        say '  building OpenVPN'
        bash "${repository}/installer/build-openvpn-macos.sh" > "${staging}/openvpn.log" 2>&1 \
            || { tail -20 "${staging}/openvpn.log"; fail 'The OpenVPN build failed.'; }
    fi

    for required in openvpn dns-updown openvpn-provenance.txt openvpn-COPYING; do
        [[ -f "${openvpn}/${required}" ]] \
            || fail "${openvpn}/${required} is missing. Run installer/build-openvpn-macos.sh."
    done

    local built="${staging}/helper"
    rm -rf "$built"

    say '  publishing the helper'
    "$dotnet" publish "${repository}/src/OpenVpnPilot.Platform.MacOS.Helper" \
        --configuration "$configuration" \
        --runtime "$runtime" \
        --self-contained true \
        --output "$built" \
        --nologo \
        -p:Version="$version" \
        > "${staging}/publish-helper.log" \
        || { tail -30 "${staging}/publish-helper.log"; fail 'Publishing the helper failed.'; }

    local binary="${built}/${HELPER_IDENTIFIER}"
    [[ -x "$binary" ]] || fail "The helper publish produced no ${HELPER_IDENTIFIER}."

    check_libraries "$binary"

    local root="${staging}/pkgroot"
    local scripts="${staging}/pkgscripts"

    rm -rf "$root" "$scripts"
    mkdir -p "${root}${HELPER_TOOLS}/${AUTHORISED_GROUP}" \
             "${root}/Library/LaunchDaemons" \
             "${root}${SUPPORT_DIRECTORY}/openvpn/sbin" \
             "${root}${SUPPORT_DIRECTORY}/openvpn/libexec" \
             "${root}${SUPPORT_DIRECTORY}/Configurations" \
             "$scripts"

    install -m 755 "$binary" "${root}${HELPER_TOOLS}/${HELPER_IDENTIFIER}"

    # The name server hook OpenVPN calls is the helper itself: the build compiled this path in as its
    # default, which is the only way a hook runs while script security stays at one.
    ln -s "../${HELPER_IDENTIFIER}" "${root}${HELPER_TOOLS}/${AUTHORISED_GROUP}/dns-updown"

    install -m 755 "${openvpn}/openvpn" "${root}${SUPPORT_DIRECTORY}/openvpn/sbin/openvpn"
    install -m 755 "${openvpn}/dns-updown" "${root}${SUPPORT_DIRECTORY}/openvpn/libexec/dns-updown"
    install -m 644 "${openvpn}/openvpn-provenance.txt" "${root}${SUPPORT_DIRECTORY}/openvpn/"
    install -m 644 "${openvpn}/openvpn-COPYING" "${root}${SUPPORT_DIRECTORY}/openvpn/"
    install -m 644 "${openvpn}/openvpn-COPYRIGHT.GPL" "${root}${SUPPORT_DIRECTORY}/openvpn/" 2> /dev/null || true
    install -m 644 "${repository}/installer/build-openvpn-macos.sh" "${root}${SUPPORT_DIRECTORY}/openvpn/build-openvpn-macos.sh"

    write_job_definition "${root}/Library/LaunchDaemons/${HELPER_IDENTIFIER}.plist"
    write_uninstall_script "${root}${SUPPORT_DIRECTORY}/uninstall.sh"
    chmod 755 "${root}${SUPPORT_DIRECTORY}/uninstall.sh"

    write_post_install "${scripts}/postinstall"
    chmod 755 "${scripts}/postinstall"

    local package="${output}/OpenVpnPilot-Helper-${version}-${runtime}.pkg"
    mkdir -p "$output"
    rm -f "$package"

    # Ownership recommended: everything in the payload is installed as root, which is what makes the
    # helper and the OpenVPN beside it something no ordinary account can replace.
    pkgbuild \
        --root "$root" \
        --scripts "$scripts" \
        --identifier "$HELPER_IDENTIFIER" \
        --version "$version" \
        --ownership recommended \
        --install-location / \
        "$package" > "${staging}/pkgbuild.log" \
        || { cat "${staging}/pkgbuild.log"; fail 'The package could not be built.'; }

    say "  wrote ${package}"
    say '  the package is not signed: signing one needs a Developer ID, and ad-hoc does not apply to packages'
}

write_job_definition() {
    cat > "$1" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>${HELPER_IDENTIFIER}</string>
    <key>ProgramArguments</key>
    <array>
        <string>${HELPER_TOOLS}/${HELPER_IDENTIFIER}</string>
    </array>
    <!--
        Started by the first connection and not before: the socket belongs to launchd, which holds it
        while nothing is running and hands it over when someone connects. The helper ends itself after
        a minute with no session, so nothing of it runs while the application is closed.
    -->
    <key>Sockets</key>
    <dict>
        <key>Listener</key>
        <dict>
            <key>SockPathName</key>
            <string>/var/run/${HELPER_IDENTIFIER}.sock</string>
            <!--
                438 is 0666. Anyone may connect, and the helper decides what they may ask for: an
                account that is not authorised can still start the configurations an administrator
                installed, which is the whole point of that rule. Refusing the connection instead
                would leave it with no way to say why.
            -->
            <key>SockPathMode</key>
            <integer>438</integer>
        </dict>
    </dict>
    <key>KeepAlive</key>
    <false/>
    <key>ProcessType</key>
    <string>Interactive</string>
</dict>
</plist>
PLIST
}

write_post_install() {
    cat > "$1" <<'SCRIPT'
#!/bin/bash
#
# Runs as root after the payload is in place. Everything it does is undone by uninstall.sh.

set -euo pipefail

readonly HELPER_IDENTIFIER='org.openvpnpilot.helper'
readonly SUPPORT_DIRECTORY='/Library/Application Support/OpenVpnPilot'
readonly COMMAND_LINK='/usr/local/bin/ovp'
readonly AUTHORISED_GROUP='openvpnpilot'

# The bundle carried the compact name until 1.3.0, so both are looked for and whichever is there is
# the one ovp is linked into. An installation made before the rename keeps working.
application=''

for candidate in '/Applications/OpenVPN Pilot.app' '/Applications/OpenVpnPilot.app'; do
    if [ -x "${candidate}/Contents/MacOS/ovp" ]; then
        application="$candidate"
        break
    fi
done

# Owned by root and writable by nobody else, because the helper runs what is in here as root. The
# directories above it are shared with other software and are left exactly as they are.
chown -R root:wheel "$SUPPORT_DIRECTORY" '/Library/PrivilegedHelperTools/openvpnpilot'
chmod 755 "$SUPPORT_DIRECTORY" "${SUPPORT_DIRECTORY}/openvpn" '/Library/PrivilegedHelperTools/openvpnpilot'

# Configurations an administrator installs for everyone. Readable by all, writable by root, so a
# standard account can start them and nobody but root can change what they say.
chmod 755 "${SUPPORT_DIRECTORY}/Configurations"

mkdir -p /Library/Logs/OpenVpnPilot
chown root:wheel /Library/Logs/OpenVpnPilot
chmod 755 /Library/Logs/OpenVpnPilot

# The group exists for accounts that are not administrators. An administrator is already authorised,
# so nothing is created for one: a group nobody needs is a change to the system for no reason.
console_user=$(stat -f '%Su' /dev/console 2>/dev/null || echo 'root')

if [ "$console_user" != 'root' ] && ! dseditgroup -o checkmember -m "$console_user" admin > /dev/null 2>&1; then
    if ! dseditgroup -o read "$AUTHORISED_GROUP" > /dev/null 2>&1; then
        dseditgroup -o create -r 'OpenVpnPilot users' "$AUTHORISED_GROUP"
        echo "Created the group ${AUTHORISED_GROUP}."
    fi

    dseditgroup -o edit -a "$console_user" -t user "$AUTHORISED_GROUP"
    echo "Added ${console_user} to ${AUTHORISED_GROUP}, so this account may start its own configurations."
fi

# ovp on PATH. A link rather than a copy, so it is the one inside the application and cannot fall
# behind it. /usr/local/bin is on the default PATH and is where a command like this belongs.
if [ -n "$application" ]; then
    mkdir -p "$(dirname "$COMMAND_LINK")"
    ln -sf "${application}/Contents/MacOS/ovp" "$COMMAND_LINK"
    echo "Linked ${COMMAND_LINK} to ${application}."
else
    echo "OpenVPN Pilot.app was not found in /Applications, so ${COMMAND_LINK} was not created."
    echo "Install the application, then run:"
    echo "  sudo ln -sf '/Applications/OpenVPN Pilot.app/Contents/MacOS/ovp' '${COMMAND_LINK}'"
fi

# Replacing a job definition means the old one has to go first, and launchctl is told to forget it
# rather than asked to reload, because a reload keeps whatever the running copy was started with.
if launchctl print "system/${HELPER_IDENTIFIER}" > /dev/null 2>&1; then
    launchctl bootout "system/${HELPER_IDENTIFIER}" || true
fi

launchctl bootstrap system "/Library/LaunchDaemons/${HELPER_IDENTIFIER}.plist"

echo 'The helper is installed. It starts when the application first connects to it.'
exit 0
SCRIPT
}

write_uninstall_script() {
    cat > "$1" <<'SCRIPT'
#!/bin/bash
#
# Removes the helper and everything the package installed. Run with sudo.
#
# The application itself is not touched: it is dragged in and dragged out, and it holds the profile
# store of whoever used it. Nothing under a home directory is touched either, for the same reason.

set -uo pipefail

readonly HELPER_IDENTIFIER='org.openvpnpilot.helper'
readonly SUPPORT_DIRECTORY='/Library/Application Support/OpenVpnPilot'
readonly COMMAND_LINK='/usr/local/bin/ovp'
readonly AUTHORISED_GROUP='openvpnpilot'

if [ "$(id -u)" != '0' ]; then
    echo 'This has to run as root: sudo "'"$0"'"' >&2
    exit 1
fi

echo 'Stopping the helper.'
launchctl bootout "system/${HELPER_IDENTIFIER}" 2>/dev/null || true

# Any tunnel it started belongs to a session that is now gone, and the helper ends those itself when
# a session closes. What is left here is the process, if it is still running.
#
# Matched by exact process name, never by command line: a pattern matched against command lines would
# also match anything that merely mentions this path, and this runs as root.
pkill -x "$HELPER_IDENTIFIER" 2>/dev/null || true

# Checked rather than trusted. Everything below deletes recursively as root, so each path is
# compared with what it has to be before anything is removed.
[ "$SUPPORT_DIRECTORY" = '/Library/Application Support/OpenVpnPilot' ] || { echo 'Refusing: the support directory is not the expected one.' >&2; exit 1; }
[ "$HELPER_IDENTIFIER" = 'org.openvpnpilot.helper' ] || { echo 'Refusing: the helper identifier is not the expected one.' >&2; exit 1; }

echo 'Removing what was installed.'
rm -f "/Library/LaunchDaemons/${HELPER_IDENTIFIER}.plist"
rm -f "/Library/PrivilegedHelperTools/${HELPER_IDENTIFIER}"
rm -rf '/Library/PrivilegedHelperTools/openvpnpilot'
rm -f "/var/run/${HELPER_IDENTIFIER}.sock"

# Only the link this package made, and only when it still points into the application.
if [ -L "$COMMAND_LINK" ] && readlink "$COMMAND_LINK" | grep -q -E 'OpenVPN Pilot\.app|OpenVpnPilot\.app'; then
    rm -f "$COMMAND_LINK"
fi

if dseditgroup -o read "$AUTHORISED_GROUP" > /dev/null 2>&1; then
    dseditgroup -o delete "$AUTHORISED_GROUP" && echo "Removed the group ${AUTHORISED_GROUP}."
fi

# Last, because the script being run is inside it.
rm -rf '/Library/Logs/OpenVpnPilot'
rm -rf "$SUPPORT_DIRECTORY"

pkgutil --forget "$HELPER_IDENTIFIER" > /dev/null 2>&1 || true

echo 'The helper is removed. The application and your profiles are untouched.'
echo ''
echo 'To remove the application as well, as the account that used it:'
echo '  rm -rf /Applications/OpenVPN\ Pilot.app'
echo '  rm -rf ~/Library/Application\ Support/OpenVpnPilot'
echo '  rm -f ~/Library/LaunchAgents/org.openvpnpilot.app.login.plist'
echo ''
echo 'The last one exists only while "start with the system" was on, and the saved sign ins are in'
echo 'the login keychain under the service OpenVpnPilot, where Keychain Access can delete them.'
exit 0
SCRIPT
}

mkdir -p "$staging" "$output"

[[ "$build_app" == 'yes' ]] && build_application
[[ "$build_helper" == 'yes' ]] && build_helper_package

say ''
say 'Artifacts'
ls -la "$output" | tail -n +2

say ''
say 'To install:'
say "  open the disk image and drag OpenVpnPilot to Applications"
say "  then: sudo installer -pkg '${output}/OpenVpnPilot-Helper-${version}-${runtime}.pkg' -target /"
say "  to remove the helper again: sudo '${SUPPORT_DIRECTORY}/uninstall.sh'"

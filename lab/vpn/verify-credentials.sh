#!/bin/sh
# Decides whether a user name and password are acceptable, including the two kinds of one time code.
#
# The point of this script is not to be a credential store. It is to make a server behave the way a
# real one does when it wants a code, because those two exchanges are the hardest part of a client to
# get right and the only way to test them is against a server that performs them.
#
# A static challenge arrives inside the password as SCRV1:base64(password):base64(response) and is
# answered in the same attempt. A dynamic challenge is refused first, with the challenge as the
# reason, and answered in the next attempt with a password of CRV1::state::response.

set -eu

. /lab/servers.sh

index="${LAB_INDEX:-0}"
user="${username:-}"
secret="${password:-}"

reject() {
    echo "lab: refused $user: $1" >&2

    # OpenVPN sends whatever is written here to the client as the reason for the refusal, which is
    # how a dynamic challenge reaches it at all.
    if [ -n "${2:-}" ] && [ -n "${auth_failed_reason_file:-}" ]; then
        printf '%s' "$2" > "$auth_failed_reason_file"
    fi

    exit 1
}

decode() {
    printf '%s' "$1" | base64 -d 2> /dev/null || true
}

if [ "$user" != "$LAB_USERNAME" ]; then
    reject "unknown user"
fi

case "$secret" in
    SCRV1:*)
        # Presented up front, answered in the same attempt.
        encoded_password=$(printf '%s' "$secret" | cut -d: -f2)
        encoded_response=$(printf '%s' "$secret" | cut -d: -f3)

        [ "$(decode "$encoded_password")" = "$LAB_PASSWORD" ] || reject "wrong password"
        [ "$(decode "$encoded_response")" = "$LAB_OTP" ] || reject "wrong code"

        echo "lab: accepted $user with a code presented up front"
        exit 0
        ;;

    CRV1::*)
        # The answer to a challenge this server raised a moment ago.
        state=$(printf '%s' "$secret" | cut -d: -f3)
        response=$(printf '%s' "$secret" | cut -d: -f5)

        [ -f "/run/lab/$state" ] || reject "unknown challenge"
        rm -f "/run/lab/$state"

        [ "$response" = "$LAB_OTP" ] || reject "wrong code"

        echo "lab: accepted $user with a code it was asked for"
        exit 0
        ;;
esac

# Six wants a code presented up front, so a plain password is not enough for it. A real server
# that expects one would not accept a client that never offered it, and one that did would make
# the test say nothing.
if [ "$index" -eq 6 ]; then
    reject "a code presented up front is required"
fi

[ "$secret" = "$LAB_PASSWORD" ] || reject "wrong password"

if [ "$index" -eq 7 ]; then
    # The password was right, and this server wants a code as well. Refusing with the challenge as
    # the reason is how the protocol asks for one; it is not a rejected credential, and a client
    # that reports it as one is wrong.
    state="state-$$"
    mkdir -p /run/lab
    : > "/run/lab/$state"

    encoded_user=$(printf '%s' "$user" | base64 | tr -d '\n')

    reject "a code is needed" "CRV1:R,E:$state:$encoded_user:Enter your token code"
fi

echo "lab: accepted $user"
exit 0

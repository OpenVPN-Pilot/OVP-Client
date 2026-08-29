#!/bin/sh
# Builds the certificate authority, the ten server and client pairs, and the ten client
# configurations that go with them.
#
# It runs once, in its own container, and everything afterwards reads what it produced. Generating
# per server would mean ten authorities, and a client would then only be usable against the one
# server it was made for by accident rather than by design.
#
# Elliptic curve keys are used throughout. They are generated in a moment rather than in minutes,
# and they remove the Diffie Hellman parameter file entirely, which is the slowest part of setting
# up an OpenVPN server by a wide margin.

set -eu

. /lab/servers.sh

PKI="/pki"
CLIENTS="/clients"
HOST="${LAB_HOST:-127.0.0.1}"

# easy-rsa clears its working directory by removing it, which it cannot do to a mount point, so it
# works in a directory of its own inside the volume. What the servers need is copied out flat, so
# nothing else has to know how easy-rsa lays its files out.
EASY="$PKI/easyrsa"

if [ -f "$PKI/.complete" ]; then
    echo "The material is already in place. Delete the ovplab-pki volume to build it again."
    exit 0
fi

echo "Building the certificate authority"

rm -rf "$PKI"/*
mkdir -p "$PKI" "$CLIENTS"

export EASYRSA="/usr/share/easy-rsa"
export EASYRSA_PKI="$EASY"
export EASYRSA_BATCH=1
export EASYRSA_ALGO=ec
export EASYRSA_CURVE=prime256v1
export EASYRSA_CERT_EXPIRE=3650
export EASYRSA_CA_EXPIRE=3650
export EASYRSA_DN=cn_only

easyrsa init-pki

# The common name is given to the authority alone. Exported, it would be applied to every request
# as well, and easy-rsa refuses a server request that carries one.
EASYRSA_REQ_CN="OpenVpnPilot lab authority" easyrsa build-ca nopass

cp "$EASY/ca.crt" "$PKI/ca.crt"

# One shared control channel key. tls-crypt hides the handshake as well as authenticating it, which
# is what a server on a public port would use.
openvpn --genkey secret "$PKI/tls-crypt.key"

for index in $LAB_SERVERS; do
    describe_server "$index"

    name=$(printf 'server-%02d' "$index")
    client=$(printf 'client-%02d' "$index")

    echo "Building $name and $client"

    easyrsa build-server-full "$name" nopass

    cp "$EASY/issued/$name.crt" "$PKI/$name.crt"
    cp "$EASY/private/$name.key" "$PKI/$name.key"

    # Only one client key carries a passphrase. It exists so the prompt for one can be exercised,
    # and having ten of them would only make the lab tiresome to use.
    if [ "$index" -eq 2 ]; then
        EASYRSA_PASSIN="pass:$LAB_KEY_PASSPHRASE" \
        EASYRSA_PASSOUT="pass:$LAB_KEY_PASSPHRASE" \
            easyrsa build-client-full "$client"
    else
        easyrsa build-client-full "$client" nopass
    fi
done

echo "Writing the client configurations to $CLIENTS"

write_client_config() {
    index="$1"
    describe_server "$index"

    client=$(printf 'client-%02d' "$index")
    file="$CLIENTS/$(printf 'lab-%02d-%s.ovpn' "$index" "$LAB_AUTH")"

    {
        echo "# OpenVpnPilot lab, server $index"
        echo "# $LAB_SUMMARY"
        echo "#"
        echo "# The site behind it is http://$LAB_SITE/"

        case "$LAB_AUTH" in
            *userpass*)
                echo "# Sign in with $LAB_USERNAME / $LAB_PASSWORD"
                ;;
        esac

        if [ "$index" -eq 2 ]; then
            echo "# The private key passphrase is $LAB_KEY_PASSPHRASE"
        fi

        case "$index" in
            6|7)
                echo "# The one time code is $LAB_OTP"
                ;;
        esac

        echo
        echo "client"
        echo "dev tun"
        echo "proto $LAB_PROTO"
        echo "remote $HOST $LAB_PORT"
        echo "resolv-retry infinite"
        echo "nobind"
        echo "persist-key"
        echo "persist-tun"
        echo "remote-cert-tls server"
        echo "verb 3"

        case "$LAB_AUTH" in
            *userpass*)
                echo "auth-user-pass"
                ;;
        esac

        if [ -n "$LAB_CLIENT_EXTRA" ]; then
            echo "$LAB_CLIENT_EXTRA"
        fi

        echo
        echo "<ca>"
        cat "$PKI/ca.crt"
        echo "</ca>"

        # A server that asks for no client certificate still gets one in the file. It is ignored
        # there, and leaving it out would make the ten configurations differ in a second way.
        echo "<cert>"
        openssl x509 -in "$EASY/issued/$client.crt"
        echo "</cert>"

        echo "<key>"
        cat "$EASY/private/$client.key"
        echo "</key>"

        echo "<tls-crypt>"
        cat "$PKI/tls-crypt.key"
        echo "</tls-crypt>"
    } > "$file"

    echo "  $file"
}

for index in $LAB_SERVERS; do
    write_client_config "$index"
done

touch "$PKI/.complete"

echo
echo "Done. Import the files in $CLIENTS."

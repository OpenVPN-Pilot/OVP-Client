#!/bin/sh
# The ten servers, described once.
#
# Both the material generator and the server entrypoint read this, so a server and the configuration
# handed to the client can never disagree about how that server authenticates.
#
# Everything here is invented. Addresses come from the documentation ranges, the credentials are
# fixed and public on purpose, and nothing in the lab is meant to leave the machine it runs on.

# Credentials, the same for every server that wants them.
LAB_USERNAME="pilot"
LAB_PASSWORD="pilot-secret"

# The passphrase on the one client key that carries one.
LAB_KEY_PASSPHRASE="key-secret"

# The answer to both kinds of one time code challenge.
LAB_OTP="123456"

# The network the sites sit on, behind every server.
LAB_SITE_NETWORK="10.9.0.0"
LAB_SITE_NETMASK="255.255.255.0"

# Describes one server. Everything is set as a variable so the caller can read what it needs.
#
# LAB_PORT          the port published on the host
# LAB_PROTO         udp or tcp
# LAB_TUNNEL        the tunnel network, one per server so ten can be up at once
# LAB_SITE          the address of the site behind this server
# LAB_AUTH          how a client proves who it is
# LAB_SERVER_EXTRA  directives added to the server configuration
# LAB_CLIENT_EXTRA  directives added to the client configuration
# LAB_SUMMARY       one line describing what this server is for
describe_server() {
    index="$1"

    LAB_PORT=$((1200 + index))
    LAB_PROTO="udp"
    LAB_TUNNEL="10.8.$index.0"
    LAB_SITE="10.9.0.$((10 + index))"
    LAB_AUTH="cert"
    LAB_SERVER_EXTRA=""
    LAB_CLIENT_EXTRA=""
    LAB_SUMMARY=""

    case "$index" in
        1)
            LAB_SUMMARY="Certificate only. The baseline: nothing to type, nothing unusual pushed."
            ;;
        2)
            LAB_AUTH="cert"
            LAB_SUMMARY="Certificate whose private key carries a passphrase, so the client has to ask for one."
            ;;
        3)
            LAB_AUTH="userpass"
            LAB_SERVER_EXTRA="verify-client-cert none"
            LAB_SUMMARY="User name and password only, with no client certificate at all."
            ;;
        4)
            LAB_AUTH="cert+userpass"
            LAB_SUMMARY="Certificate and a user name and password together."
            ;;
        5)
            LAB_AUTH="userpass"
            LAB_PROTO="tcp"
            LAB_SERVER_EXTRA="verify-client-cert none"
            LAB_SUMMARY="The same as three, over TCP instead of UDP."
            ;;
        6)
            LAB_AUTH="cert+userpass"
            LAB_CLIENT_EXTRA='static-challenge "Enter your token code" 1'
            LAB_SUMMARY="A one time code presented up front, answered in the same attempt."
            ;;
        7)
            LAB_AUTH="userpass"
            LAB_SERVER_EXTRA="verify-client-cert none"
            LAB_SUMMARY="A one time code raised as the reason for a refusal, answered in the next attempt."
            ;;
        8)
            LAB_AUTH="cert"
            LAB_SERVER_EXTRA='push "dhcp-option DNS 10.9.0.53"
push "dhcp-option DOMAIN lab.example"
push "route 198.51.100.0 255.255.255.0"'
            LAB_SUMMARY="Pushes name servers, a search domain and a second route, none of which exist."
            ;;
        9)
            LAB_AUTH="cert"
            LAB_SERVER_EXTRA='push "redirect-gateway def1 bypass-dhcp"'
            LAB_SUMMARY="Asks to carry all traffic. Route protection is what stops it."
            ;;
        10)
            LAB_AUTH="cert"
            LAB_SERVER_EXTRA="push \"route $LAB_SITE_NETWORK $LAB_SITE_NETMASK\"
compress lzo
push \"compress lzo\""
            LAB_SUMMARY="Pushes the whole site network and a compression setting a modern client refuses."
            ;;
        *)
            echo "There is no server $index." >&2
            return 1
            ;;
    esac

    # Every server routes to its own site and to nothing else, so ten tunnels can be up together
    # without any of them deciding where the others' traffic goes. Nine and ten break that on
    # purpose, which is the point of having them.
    LAB_PUSH_SITE="push \"route $LAB_SITE 255.255.255.255\""
}

# The servers this lab runs, in order.
LAB_SERVERS="1 2 3 4 5 6 7 8 9 10"

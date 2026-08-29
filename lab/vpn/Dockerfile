# The image both the material generator and the ten servers run.
#
# One image rather than two: the generator needs the same OpenVPN and easy-rsa the servers do, and a
# second image would be a second thing to keep in step for no gain.
FROM alpine:3.20

RUN apk add --no-cache \
        openvpn \
        easy-rsa \
        iptables \
        openssl \
        bash

# The package installs the tool under share rather than on the path.
RUN ln -s /usr/share/easy-rsa/easyrsa /usr/local/bin/easyrsa

COPY entrypoint.sh /usr/local/bin/entrypoint.sh
COPY generate.sh /usr/local/bin/generate.sh
COPY verify-credentials.sh /usr/local/bin/verify-credentials.sh

RUN chmod +x /usr/local/bin/entrypoint.sh \
             /usr/local/bin/generate.sh \
             /usr/local/bin/verify-credentials.sh

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]

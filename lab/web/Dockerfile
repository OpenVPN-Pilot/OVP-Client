# One site, reachable only through the tunnel in front of it.
#
# It exists to give a tunnel something to carry. A client that reports throughput, round trip and
# routes has nothing to report against a server that answers nothing.
FROM nginx:1.27-alpine

# The stock image ships a default server on the same port, and two of those do not start.
RUN rm -f /etc/nginx/conf.d/default.conf

COPY nginx.conf.template /etc/nginx/templates/site.conf.template
COPY entrypoint.d/ /docker-entrypoint.d/

RUN chmod +x /docker-entrypoint.d/*.sh

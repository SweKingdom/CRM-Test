#!/usr/bin/env sh
# Renders Keycloak realm templates using .env. Usage: scripts/render-realms.sh [env-file]
exec node "$(dirname "$0")/render-realms.mjs" "$@"

# Renders Keycloak realm templates using .env. Usage: scripts/render-realms.ps1 [env-file]
node (Join-Path $PSScriptRoot 'render-realms.mjs') @args
exit $LASTEXITCODE

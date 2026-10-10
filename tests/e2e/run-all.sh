#!/bin/sh
# Builds the harness and runs every end-to-end check. Needs: dotnet 8 SDK, node, playwright (NODE_PATH), CHROMIUM_PATH if its browser isn't installed.
set -e
cd "$(dirname "$0")"
dotnet build E2E.Harness.csproj -c Debug -o out -v q --nologo
node verify.cjs
node verify-plugin.cjs

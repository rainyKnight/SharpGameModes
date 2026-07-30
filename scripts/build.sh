#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

dotnet restore SharpGameModes.slnx
dotnet build SharpGameModes.slnx --configuration Release --no-restore --disable-build-servers

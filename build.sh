#!/bin/sh
# Test build under mono (WSL). Never for releases; release build = build.cmd on Windows.
# Usage: ./build.sh [test [filter]]
set -e
cd "$(dirname "$0")"
mkdir -p bin
FLAGS="-nologo -langversion:5 -target:exe -optimize+ -r:System.Web.Extensions.dll"
mcs $FLAGS -out:bin/agent-relay-claude.exe src/core/*.cs src/agents/claude/*.cs
mcs $FLAGS -out:bin/agent-relay-codex.exe src/core/*.cs src/agents/codex/*.cs
mcs $FLAGS -out:bin/agent-relay-tests.exe src/core/*.cs src/agents/*/*Rules.cs tests/*.cs
[ -f bin/agent-relay.json ] || cp examples/agent-relay.json bin/
if [ "$1" = "test" ]; then
    shift
    exec mono bin/agent-relay-tests.exe "$@"
fi

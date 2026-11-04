#!/bin/sh
# T-L7 fake agent: prints what the agent process received from wsl.exe.
printf 'cwd=[%s]\n' "$(pwd)"
printf 'A=[%s] B=[%s]\n' "${A-unset}" "${B-unset}"
printf 'argc=%s\n' "$#"
for a in "$@"; do printf 'arg=[%s]\n' "$a"; done

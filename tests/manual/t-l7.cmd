@echo off
rem T-L7 (SPEC 6.1, 12.4): what wsl.exe passes to the agent - cwd, env, args.
rem Needs: build.cmd done, the funspintest dir mounted in WSL (fstab). Paths in t-l7.json are machine-specific.
rem Run:   tests\manual\t-l7.cmd > bin\t-l7.txt 2>&1
setlocal
chcp 65001 >nul
cd /d "%~dp0..\.."
set T=%CD%\bin\t-l7
if not exist "%T%\work dir" mkdir "%T%\work dir"
if not exist "%T%\no env" mkdir "%T%\no env"
copy /y bin\agent-relay-claude.exe "%T%\" >nul || exit /b 1
copy /y tests\manual\t-l7.json "%T%\agent-relay.json" >nul || exit /b 1

echo ===== 1. with env (expect A=[1] B=[x y], cwd .../bin/t-l7/work dir)
cd /d "%T%\work dir"
"%T%\agent-relay-claude.exe" --relay-self-test
"%T%\agent-relay-claude.exe" "$HOME;x" "a b" "it's" \"q\" "*" "`id`" café "" "x\\" "a&b|c"
echo exit=%ERRORLEVEL%

echo ===== 2. no env (expect A=[unset] B=[unset], cwd .../bin/t-l7/no env)
cd /d "%T%\no env"
"%T%\agent-relay-claude.exe" --relay-self-test
"%T%\agent-relay-claude.exe" "$HOME;x" "a b" "it's" \"q\" "*" "`id`" café "" "x\\" "a&b|c"
echo exit=%ERRORLEVEL%

echo ===== expected args (both runs): argc=10
echo   [$HOME;x] [a b] [it's] ["q"] [*] [`id`] [café] [] [x\] [a^&b^|c]

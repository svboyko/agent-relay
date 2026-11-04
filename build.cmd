@echo off
rem Release build with the .NET Framework compiler bundled with Windows.
rem Usage: build.cmd [test [filter]]
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo build: csc.exe not found: %CSC% 1>&2
    exit /b 1
)
cd /d "%~dp0"
if not exist bin mkdir bin
set FLAGS=/nologo /langversion:5 /target:exe /optimize+ /reference:System.Web.Extensions.dll
"%CSC%" %FLAGS% /out:bin\agent-relay-claude.exe src\core\*.cs src\agents\claude\*.cs || exit /b 1
"%CSC%" %FLAGS% /out:bin\agent-relay-codex.exe src\core\*.cs src\agents\codex\*.cs || exit /b 1
"%CSC%" %FLAGS% /out:bin\agent-relay-tests.exe src\core\*.cs /recurse:src\agents\*Rules.cs tests\*.cs || exit /b 1
if not exist bin\agent-relay.json copy examples\agent-relay.json bin\ >nul
if /i not "%~1"=="test" exit /b 0
bin\agent-relay-tests.exe %2
exit /b %ERRORLEVEL%

# Requirements

`(Vn)` = source in VISION.md.

## Build
- **R1:** Build = `build.cmd` → `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`. No SDK, MSBuild, NuGet. (V15)
- **R2:** C# 5 only. Refs: .NET Framework 4.x assemblies only (JSON = `System.Web.Extensions`). (V16)
- **R3:** Core = source files compiled into each exe. No shared DLL. Install = exe + config. (V14, V15)
- **R4:** `build.cmd` → `agent-relay-claude.exe`, `agent-relay-codex.exe`, `agent-relay-tests.exe`. Compile fail → exit ≠ 0. (V5, V17)
- **R5:** New agent = new entry dir + rules class + build line. Zero core edits. (V14)

## Launch
- **R6:** Child = `%SystemRoot%\System32\wsl.exe -d <distro> --cd <linuxCwd> -- [env K=V …] <agentExe> <args…>`. Nothing else. No native mode, no `cmd.exe`. (V1, V3, V8)
- **R7:** WSL side needs no automount, no interop, no Windows PATH. (V3)
- **R8:** `linuxCwd` = launch dir mapped via project mappings. Always set. (V8)
- **R9:** Agent args forwarded verbatim; Windows quoting safe for spaces, quotes, backslashes, Unicode. (V10)
- **R10:** Env overrides → `env K=V` prefix inside WSL. No `WSLENV`. (V3, V6)
- **R11:** Relay-own flags only with `--relay-` prefix (`--relay-version`, `--relay-self-test`). All other args → agent (e.g. `--version` goes to agent). (V11)

## Config
- **R12:** `agent-relay.json` beside exe. Only source. Read once at start. (V6)
- **R13:** Shape: `projects[]` { `windowsRoot`, `linuxRoot`, `distribution`, `pathMappings[]`, `env{}`, `log{}`, `agents{ <name>: { executable, env{}, log{} } }` }. Optional top-level `defaults` (same fields); project wins, agent wins over project. (V6)
- **R14:** Validate at start: JSON ok, required fields, Win path = drive/UNC absolute, Linux path absolute ≠ `/`, no duplicate roots, agent section exists. (V7)
- **R15:** Project select: launch dir, case-insensitive, dir boundary, longest root wins. No match → error. No `/mnt/<drive>` fallback. (V7, V13)
- **R16:** Any config/select error → stderr `agent-relay-<agent>: <msg>`, exit 1, no child started. (V7)

## Translation
- **R17:** Mappings = project root pair + `pathMappings`. Bidirectional, longest prefix, boundary-aware. Win compare case-insensitive, Linux case-sensitive. Unmapped → unchanged. (V9, V13)
- **R18:** Shared JSON walker in core. Per-agent rules: path fields, opaque fields, `ShouldTranslate(args)`. (V9, V14)
- **R19:** Translate only string values of rule path fields. Never: keys, numbers, text, file content, commands, diffs, URIs. (V9)
- **R20:** Untouched tokens byte-identical; big numeric IDs exact. (V9)
- **R21:** Parse/translate error → forward original line, log error. Never drop a line. (V9, V10)
- **R22:** Claude: translate only if args contain `stream-json`; else passthrough. (V9)
- **R23:** Codex: path-field list taken from captured real traffic. No blanket prefix replace. (V9)

## Relay
- **R24:** stdin → child, child stdout → stdout: line-framed UTF-8, no BOM, `\n`, flush per line. (V10)
- **R25:** Child stderr → stderr as raw bytes, untranslated. (V10)
- **R26:** stdout = protocol only. Diagnostics → stderr or log. (V10)
- **R27:** Child exit → drain stdout/stderr → exit with child code. stdin EOF → close child stdin. Open stdin after child exit → no hang. (V11)
- **R28:** Child start fail → stderr msg, exit 1. (V11)
- **R29:** Blocking reads, no polling; idle CPU ≈ 0. No delay beyond one line. (V2, V10)

## Logging
- **R30:** Off by default. Enable + path per project/agent; relative path → beside exe. Default `agent-relay-<agent>.log`. (V12)
- **R31:** Entry = ISO timestamp, session id, direction, received + forwarded line. (V12)
- **R32:** Launch entry: cwd, project, args, `wsl.exe` cmdline, env. Mask names containing KEY/TOKEN/SECRET/PASSWORD/AUTH/CREDENTIAL. (V4, V12)
- **R33:** Size cap (config, default 10 MB) → keep 1 rotated file. (V12)
- **R34:** Log write fail → one stderr warning, relay continues. (V12)

## Security
- **R35:** Relay never mounts, copies, syncs files or widens access. (V4, V13)
- **R36:** Docs warn: logs = full conversation content. (V4, V12)

## Tests
- **R37:** `agent-relay-tests.exe`: no deps; exit 0 = pass, 1 = fail; failures listed. (V17)
- **R38:** Unit cover: quoting, config validation, project select, mappings (custom, case, boundary, `C:` root, `/mnt/cat` ≠ `/mnt/c`), content preservation, big IDs, per-agent rules, past failures. (V17)
- **R39:** Launcher seam → lifecycle tests with fake child exe: EOF both ways, exit code, crash, start fail, no hang (timeout). (V11, V17)

## Docs
- **R40:** README: build, install, config reference, isolation prereqs (`wsl.conf` automount/interop off, `fstab` drvfs mounts), file-access setup, agent install in WSL. (V18)
- **R41:** README Zed: `agent_servers` in global settings only; `CLAUDE_CONFIG_DIR=\\wsl.localhost\<distro>\home\<user>\.claude` (Windows side). (V18)
- **R42:** `examples/agent-relay.json` passes R14. (V18)

## Open Questions
- **Q1:** Interop off → agents in WSL cannot run `csc`/tests. Manual Windows builds only, or optional WSL compile of core (mono)?
- **Q2:** V2 metric — what is measured (CPU %, I/O, latency)?
- **Q3:** R13 `defaults` — needed, or explicit per project only?

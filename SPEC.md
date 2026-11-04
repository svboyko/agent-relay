# Agent Relay — Specification

Status: draft 7 (2026-11-03). Reasons for decisions: `DECISIONS.md` (Dn). Pending checks: `TODO.md`.
Sources: `VISION.md` (Vn), `REQUIREMENTS.md` (Rn, Qn).

---

## 1. Overview

Small Windows console programs between an editor (Zed, native Windows) and a coding agent
(Claude Code, Codex) running in an isolated WSL distribution.

```
Zed (Windows)                agent-relay-<agent>.exe (Windows)            WSL distro
 agent adapter  ── stdin ──▶  select project → launch wsl.exe ── stdin ──▶  agent CLI
                ◀─ stdout ──  translate paths per line          ◀─ stdout ──  (Linux paths)
                ◀─ stderr ──  raw passthrough                  ◀─ stderr ──
```

The relay:
1. selects a project from its Windows launch directory;
2. starts the agent through `wsl.exe` with an explicit Linux working directory;
3. relays newline-delimited JSON both ways, translating strings and keys that start with a mapped
   folder (§8); everything else is forwarded unchanged;
4. exits with the child's exit code.

It never mounts, copies, or synchronizes files (V13, R35).

| Artifact | Purpose |
|---|---|
| `agent-relay-claude.exe` | Relay for Claude Code (`CLAUDE_CODE_EXECUTABLE` replacement) |
| `agent-relay-codex.exe` | Relay for Codex (`codex app-server`) |
| `agent-relay-tests.exe` | Unit + lifecycle tests; also the fake child process |
| `agent-relay.json` | Single config file, beside the executables |
| `build.cmd` | Windows build (csc only) |
| `build.sh` | Optional WSL test build with mono (Q1), never for releases |
| `README.md`, `examples/agent-relay.json` | Documentation |

Terms: **agent name** = `claude` | `codex`, fixed per exe. **Launch dir** = relay's current directory
at start. **Mapping** = (`windows`, `linux`) root pair. **Rules** = per-agent class (§2.1).
**Line** = bytes up to `\n` (0x0A).

---

## 2. Source layout (V14, R3, R5)

```
agent-relay/
  build.cmd  build.sh
  src/core/
    RelayMain.cs        args → config → resolve → launch → pump → exit
    RelayArgs.cs        --relay-* flags
    Config.cs           JSON load, strict validation, merge
    ProjectResolver.cs  launch dir → resolved project / default mode
    PathMapper.cs       bidirectional longest-prefix mapping
    JsonPathWalker.cs   token-preserving JSON walker
    AgentRules.cs       rules base class
    ArgQuoter.cs        Windows command-line quoting
    LaunchSpec.cs       FileName, Arguments, WorkingDirectory, Environment
    ChildLauncher.cs    IChildLauncher / IChildProcess seam, Process + Job Object implementation
    LinePump.cs         line-framed translating pump
    RawPump.cs          raw byte pump
    Logger.cs           per-session log file, masking, pruning
    Version.cs
  src/agents/claude/  Program.cs  ClaudeRules.cs
  src/agents/codex/   Program.cs  CodexRules.cs
  tests/              TestMain.cs (runner, --fake-child)  *Tests.cs
  examples/agent-relay.json
```

- Core files are compiled into every exe; no shared DLL. Install = exe + `agent-relay.json`.
- Core refers to agents only through `AgentRules`. Program.cs = `RelayMain.Run("claude", new ClaudeRules(), args)`.
- New agent = `src/agents/<name>/` (Program.cs + Rules) + one line in `build.cmd` (+ `build.sh`).
  No core edits, no plugin framework, no reflection.

### 2.1 AgentRules

```csharp
abstract class AgentRules
{
    public abstract string Name { get; }                       // "claude"
    public abstract bool ShouldTranslate(string[] args);       // false → raw passthrough
    public abstract bool IsOpaqueField(string name, string parentName); // never translate value + descendants
}
```

`parentName` = member name of the enclosing object (`""` at root). Array elements inherit the
array's member name.

---

## 3. Build (V15, V16, R1–R4, Q1)

- `build.cmd`: `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (missing → message, exit 1).
  Flags `/nologo /langversion:5 /target:exe /optimize+ /reference:System.Web.Extensions.dll`.
- Output to `bin\`: the three exes. Each = `src\core\*.cs` + its entry dir. Tests = core + all
  `src\agents\*\*Rules.cs` + `tests\*.cs`.
- Copies `examples\agent-relay.json` to `bin\` only if not present there.
- Compile failure → `exit /b 1`. `build.cmd test` → build, run tests, return their exit code.
- C# 5 only: no interpolation, `?.`, `nameof`, expression-bodied members, auto-property
  initializers, `using static`, tuples, `out var`. No `async`/`await`: threads + blocking I/O (R29).
  JSON via `System.Web.Extensions` for decoding/validation only, never to re-serialize messages.
- `build.sh` (optional): `mcs -langversion:5 -r:System.Web.Extensions …`, same three exes. Tests
  marked **[win]** (Job Object, `CommandLineToArgvW`) report `SKIP` under mono. Job Object calls
  run only on `Win32NT`. Release check = full suite on Windows.

---

## 4. Configuration (V6, V7, R12–R16, Q2)

### 4.1 Loading

- `agent-relay.json` in `AppDomain.CurrentDomain.BaseDirectory`. No other source (R12, D9).
- Read once at start, UTF-8 (BOM tolerated).
- `JavaScriptSerializer.DeserializeObject`, then strict validation by hand. Unknown members are errors.

### 4.2 Schema

```jsonc
{
  "defaults": {                          // optional
    "mode": "none",                      // "none" | "wsl" | "windows"; default "none" (§4.4)
    "distribution": "Ubuntu",
    "automountRoot": "/mnt",             // mode "wsl" only; default "/mnt"
    "pathMappings": [ { "windows": "D:\\Shared", "linux": "/shared" } ],
    "env": { "NAME": "value" },
    "log": { "level": "off", "dir": "logs", "keep": 20, "maxSizeMB": 10 },
    "agents": {
      "claude": {
        "executable": "/home/me/.local/bin/claude",
        "windowsExecutable": "C:\\Users\\me\\.local\\bin\\claude.exe",   // defaults only
        "env": {},
        "log": { "level": "basic" }
      }
    }
  },
  "projects": [                          // optional if defaults.mode is "wsl" or "windows"
    {
      "windowsRoot": "C:\\Users\\me\\src\\app",   // required
      "linuxRoot": "/home/me/src/app",            // required
      "distribution": "Ubuntu",                   // required unless in defaults
      "pathMappings": [],
      "env": {},
      "log": { "dir": "D:\\logs\\app" },
      "agents": { "claude": { "executable": "/home/me/.local/bin/claude", "env": {}, "log": {} } }
    }
  ]
}
```

| Field | Rule |
|---|---|
| `mode`, `automountRoot`, `windowsExecutable` | `defaults` only. |
| `distribution` | Non-empty, no whitespace or quotes. |
| `windowsRoot`, `pathMappings[].windows` | Drive-absolute (`C:\…`, `C:/…`) or UNC (`\\server\share…`). |
| `linuxRoot`, `pathMappings[].linux`, `automountRoot` | Absolute Linux path ≠ `/`. |
| `env` | Object string→string; names `^[A-Za-z_][A-Za-z0-9_]*$`. |
| `agents.<name>.executable` | Absolute Linux path. |
| `agents.<name>.windowsExecutable` | Absolute Windows path to an `.exe`, `%VAR%` expanded. |
| `log.level` | `off` \| `basic` \| `changes` \| `full` (§10.2). Default `off`. |
| `log.dir` | Windows dir, absolute or relative to exe dir. Default `logs`. |
| `log.keep` | int > 0, session files kept per agent (best effort, §10.1). Default 20. |
| `log.maxSizeMB` | number > 0, cap per session file. Default 10. |

`log` is allowed in `defaults`, `projects[]` and any agent section. It holds settings only; files are
always per agent and per session (§10.1, D8).

### 4.3 Merge

For agent `<name>`:
- `distribution`: project, else defaults.
- `pathMappings`: defaults + project, concatenated. Duplicate Windows or Linux roots → error.
- Agent fields `executable`, `env`, `log`: each taken **whole** from `project.agents.<name>`, else
  from `defaults.agents.<name>` (D12).
- Effective `env`: `defaults.env`, then `project.env`, then agent `env`; later key wins.
  No way to remove an inherited key (`null` not allowed).
- Effective `log`: `defaults.log`, then `project.log`, then agent `log`; later field wins, missing
  fields → built-in defaults.

### 4.4 Default mode (Q2, D4)

Used only when **no project matches** the launch dir.

| `defaults.mode` | Behavior |
|---|---|
| `none` (default) | stderr `agent-relay-<agent>: no project in <config> matches <launchDir> (defaults.mode is "none")`, exit 1. |
| `wsl` | WSL launch with a synthesized project: `linuxRoot` = `<automountRoot>/<drive lowercase>/<rest with />` (`C:\Users\me\x` → `/mnt/c/Users/me/x`). Mappings = `X:\` ↔ `<automountRoot>/x` for every drive letter + `defaults.pathMappings`. Everything else from `defaults`. UNC launch dir → error. Needs automount enabled (no isolation). |
| `windows` | Native Windows launch, no WSL, no translation (raw passthrough). Uses `defaults.env`, `defaults.log` + `defaults.agents.<name>` (`windowsExecutable`, `env`, `log`). Executable = `windowsExecutable`, else `<name>.exe` searched in `PATH` directories (full path resolved by the relay). Not found → error suggesting `windowsExecutable`. |

Mode `windows` guards: non-`.exe` target (e.g. `.cmd`) → error asking for the real `.exe`;
target is the relay itself (same full path, or name starts with `agent-relay-`) → error.

### 4.5 Validation (R14) — at start, before any child

1. File exists, valid JSON, root is an object.
2. Unknown member or wrong type → error with JSON path (`projects[1].logEnabled: unknown field`).
3. `projects` empty/absent only allowed with mode `wsl` or `windows`.
4. Each project: valid roots (§4.2), effective `distribution` present.
5. Mappings valid and normalized: Windows `/`→`\`, trailing `\` trimmed (`C:\` → `C:`); Linux
   trailing `/` trimmed.
6. No two projects with equal `windowsRoot` (case-insensitive).
7. Selected project (or mode `wsl`) must resolve an `executable` for **this** agent, else
   `agent-relay-claude: project C:\… has no agents.claude.executable`. Other agents: shape only.
8. Mode `windows`: resolved executable exists and ends with `.exe`.

Structural errors anywhere fail every invocation.

### 4.6 Project selection (V7, R15)

1. Normalize the launch dir like `windowsRoot`.
2. Candidates: `launchDir == windowsRoot` or starts with `windowsRoot + "\"` (ordinal,
   case-insensitive). `C:` matches `C:\anything`.
3. Longest root wins (duplicates rejected, so no ties).
4. No candidate → default mode (§4.4).

### 4.7 Errors (R16)

One stderr line `agent-relay-<agent>: <message>` (with config path when relevant), exit 1, no
child, nothing on stdout.

---

## 5. Command line (R11)

Relay flags are recognized **only as the first argument**:
- `--relay-version` → stdout `agent-relay-<agent> <version>`, exit 0.
- `--relay-self-test` → validate config, resolve project/mode, print effective settings, exact
  launch command line and env (masked, §10.3); run quick checks (quoting, launch-dir mapping round
  trip). No agent start. Exit 0 if OK, else 1.
- `--relay-help` → usage, exit 0.
- Other `--relay-*` → error, exit 1.

Everything else (including `--version`, `--help`, `-p`) goes to the agent verbatim.

---

## 6. Launch (V1, V3, V8, R6–R10, R28)

### 6.1 WSL (matched project or mode `wsl`)

```
FileName : %SystemRoot%\System32\wsl.exe
Arguments: -d <distro> --cd <linuxCwd> --exec env [K=V …] <agentExe> <args…>
WorkingDirectory: launch dir
```

- `linuxCwd` = launch dir mapped Windows→Linux (always set, R8).
- `env K=V …` only if effective env is non-empty; else `--exec <agentExe> …`. No `WSLENV`.
- Every token quoted with ArgQuoter (§6.3).
- `WSL_UTF8=1` set in the `wsl.exe` process environment (Windows side only) (D10).
- No `cmd.exe`, no Windows `PATH` inside WSL. Works with automount and interop disabled.

**Pending T-L7 (§12.4):** variant A above (`--exec`, no shell) is preferred. Fallback variant B:
`-- env [K=V…] <exe> <args…>` with every token POSIX single-quoted (`'` → `'\''`). The form is built
only in `LaunchSpec.BuildWslArguments`. The spec, R6 and README are updated after the test.

### 6.2 Windows (mode `windows`)

FileName = resolved `.exe`; Arguments = agent args (ArgQuoter); WorkingDirectory = launch dir;
Environment = inherited + effective env.

### 6.3 Quoting (R9)

Inverse of `CommandLineToArgvW`:
- Non-empty token without whitespace or `"` → unquoted (distro names must stay unquoted, D10).
- Otherwise wrap in `"`; backslashes before `"` and before the closing quote are doubled; `"` → `\"`.
- Empty → `""`. Unicode unchanged.

### 6.4 Process start

`UseShellExecute=false`, `CreateNoWindow=true`, all three streams redirected, raw `BaseStream`s.
`Process.Start` throws → stderr `agent-relay-<agent>: cannot start <FileName>: <message>`, log, exit 1.

---

## 7. Relay I/O (V10, R24–R26, R29)

Three threads, blocking reads only (idle CPU ≈ 0):

| Pump | From → To | Translating | Passthrough / mode `windows` |
|---|---|---|---|
| input | stdin → child stdin | lines, Windows→Linux | raw bytes |
| output | child stdout → stdout | lines, Linux→Windows | raw bytes |
| error | child stderr → stderr | raw bytes | raw bytes |

- Raw pump: 8 KB buffer, write + flush each read chunk.
- Line pump: split on 0x0A only; lines unbounded. Strict UTF-8 decode; invalid UTF-8 → original
  bytes. Trailing `\r` kept. Write line + `\n`, flush per line. No BOM. Final unterminated
  fragment at EOF forwarded as-is, no `\n` added.
- stdout carries only child stdout. Diagnostics → stderr or log.

---

## 8. Path translation (V9, R17–R23, D1–D3)

### 8.1 PathMapper

Mappings: project root pair first, then `pathMappings`.

Windows→Linux:
1. Skip if value has `\r`/`\n` or is not `^[A-Za-z]:[\\/]`, `^[A-Za-z]:$`, `^\\\\`.
2. Compare with `/`→`\`; longest `windows` root where value == root or starts with `root + "\"`
   (ordinal, case-insensitive).
3. Result = `linux` + suffix with `\`→`/`.

Linux→Windows:
1. Skip if value has `\r`/`\n` or doesn't start with `/`.
2. Longest `linux` root where value == root or starts with `root + "/"` (ordinal, case-sensitive).
3. Result = `windows` + suffix with `/`→`\`; bare `C:` → `C:\`.

Unmatched → unchanged. Boundary rule: `/mnt/cat` ≠ `/mnt/c`, `C:\app-other` ≠ `C:\app`.

### 8.2 JsonPathWalker

- Hand-written tokenizer. Tokens, whitespace and numbers copied verbatim; untouched lines are
  byte-identical; `9007199254740993` keeps its digits (R20).
- Every string value and object key outside opaque members goes to PathMapper; translated if it
  starts with a mapped root + end/separator (D1, D3). Paths later in a string and URIs never match.
- Changed strings are re-encoded escaping only `"`, `\`, U+0000–U+001F (not `JavaScriptSerializer.Serialize`).
- Context per value: member name, parent member name, opaque flag; arrays inherit the member name.
- Opaque members (`rules.IsOpaqueField`) are copied verbatim with all descendants (D2).
- Log level `changes` or `full`: each translation is logged (§10.2).
- Line must be exactly one JSON value + whitespace, else error.
- Any error → forward the **original** line bytes, log `TRANSLATION ERROR` (R21).

### 8.3 Claude rules (R22)

- `ShouldTranslate`: any argument contains `stream-json`.
- Opaque: `text`, `old_string`, `new_string`, `command`, `partial_json`, `stdout`, `stderr`,
  `content` **except** when parent is `message` (so `message.content[]` blocks are walked and
  `tool_use.input.file_path` is translated; `tool_use.input.content` and `tool_result.content` are not).
- Path fields seen (test vectors): `cwd`, `original_cwd`, `path`, `file_path`, `filePath`,
  `transcript_path`, `persistedOutputPath`, `user_output_styles_dir`.

### 8.4 Codex rules (R23)

- `ShouldTranslate`: args contain `app-server`.
- Opaque: `aggregatedOutput`, `text`, `delta`, `command`, `diff`, `unifiedDiff`, `content`,
  `output`, `stdout`, `stderr`, `summary`, `message`, `instructions`, `baseInstructions`,
  `developerInstructions`. Starting list; reviewed against a full session (TODO.md).
- Path fields seen (test vectors): `cwd`, `cwds`, `path`, `runtimeWorkspaceRoots`, keys of `projects`.

---

## 9. Lifecycle and exit (V11, R27, D5, D6)

1. Before starting the child (Windows, real launcher only): create a Job Object with
   `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and assign the **relay process itself** to it. The child and
   all its descendants are then job members from creation; there is no window to escape (D5).
   Job creation or assignment fails → stderr `agent-relay-<agent>: cannot create job object:
   <message>`, exit 1, no child started.
   If the relay dies, its job handle closes and Windows kills every job member.
2. Start the child and the three pumps (background threads).
3. Relay stdin EOF → close child stdin. No kill (D6).
4. Child exits → cleanup: terminate every job process except the relay
   (`JobObjectBasicProcessIdList`); repeat at most 5 rounds until only the relay is left → join
   output and error pumps until EOF → flush stdout/stderr → exit with the child's exit code
   (incl. `-1`). Do not wait for the input pump (stdin may stay open).
   **Cleanup failure** (an API call fails, or processes remain after 5 rounds): stderr
   `agent-relay-<agent>: cleanup failed: <message>`, log `ERROR`, join the pumps for at most 5 s,
   flush, exit with the child's exit code. Data a remaining descendant still sends is lost. On exit
   the job handle closes and Windows kills the remaining members (step 1).
   Cleanup is behind the `IChildProcess` seam, so tests can make it fail.
5. Write to child stdin after its exit (broken pipe) → input pump stops, logs once.
6. Relay stdout closed by parent → output pump keeps reading child stdout and discards, logs once.
7. Ctrl+C / console close: not intercepted.

Pending (TODO.md): verify the Linux agent ends when the job kills `wsl.exe`.

---

## 10. Logging (V12, R30–R34, R36, D8)

### 10.1 Files

- Effective `log` from §4.3. Level `off` by default → no file. No shared file: every relay process
  writes its own.
- One file per relay process: `<dir>\agent-relay-<agent>-<yyyyMMdd-HHmmss>-<session8>.log`
  (`session8` = first 8 hex of the session id). Dir created if missing.
- At start, delete the oldest `agent-relay-<agent>-*.log` in `<dir>` until `keep` files remain
  including the new one. **Best effort:** files in use (running sessions) and deletion errors are
  skipped, so more than `keep` files can exist with many concurrent sessions or concurrent starts.
- Size cap per file, checked **before** each write: if current size + entry size would exceed
  `maxSizeMB`, the entry is skipped, one short `LOG LIMIT` entry is written instead, and logging stops
  for this session (relay continues). The file never exceeds the cap by more than that entry; a
  single entry larger than the cap is never written.
- Opened once, append, `FileShare.Read`; flushed per entry; one in-process lock for the threads.

### 10.2 Levels and entries (R31)

Levels are cumulative: each includes everything of the level above it.

| Level | Entries | Content in log |
|---|---|---|
| `off` | none, no file | — |
| `basic` | `LAUNCH` (§10.3), `EOF`, `EXIT` (code), `ERROR`, `TRANSLATION ERROR` (direction, message, line length — not the line), `AGENT STDERR`, `LOG LIMIT` | no protocol message bodies; launch args/env (§10.3) and agent stderr may contain content (e.g. `claude -p "<prompt>"`) |
| `changes` | + `<direction> CHANGE` per translated value: `field=<member>` (or `key in <member>`), old → new as JSON strings | translated values (paths, or content translated by mistake) |
| `full` | + `<direction> RECEIVED` / `FORWARDED` per line; forwarded == received → `(unchanged)`. Raw pumps log each chunk as `RECEIVED` | full conversation |

`<direction>` = `EDITOR -> AGENT` or `AGENT -> EDITOR`. Stderr and raw chunks are decoded as UTF-8
for the log only (invalid bytes replaced); forwarded bytes are never changed.

Format: UTF-8, no BOM:

```
[2026-11-03T20:45:01.1234567+01:00 session=<32-hex> <ENTRY>]
<payload>
```

### 10.3 Launch entry (R32)

Launch dir, project root or mode, version, agent args (one per line), `FileName` + `Arguments`,
effective env, inherited environment. Values of names containing `KEY`, `TOKEN`, `SECRET`,
`PASSWORD`, `AUTH`, `CREDENTIAL` (case-insensitive) → `<masked, N chars>`, also inside `env K=V`
tokens of the command line.

### 10.4 Failures (R34)

Any log failure → one stderr warning `agent-relay-<agent>: log disabled: <message>`, logging off for
the rest of the process, relay continues.

---

## 11. Security (V4, V13, R35, R36)

- No mounting, copying, syncing, or permission changes. Mappings only rewrite strings.
- Translation is not a security boundary; the agent can access whatever its distro can.
- Recommended `/etc/wsl.conf`: `[automount] enabled=false`, `[interop] enabled=false
  appendWindowsPath=false`; project access via `/etc/fstab` drvfs entries or Linux-native checkouts.
- Modes `wsl` and `windows` are not isolated; README says so.
- Logs contain full conversation and file content; masking covers env variables only.

---

## 12. Tests (V17, R37–R39)

### 12.1 Runner

`agent-relay-tests.exe [filter]`: named static test methods in a list; prints `PASS/FAIL/SKIP <name>`,
expected/actual on failure, summary. Exit 0 = all pass, 1 = any fail.

### 12.2 Unit tests

| ID | Cases |
|---|---|
| T-Q Quoting | simple, empty, spaces, `"`, trailing `\`, `\"` combos, Unicode, distro unquoted; **[win]** round trip via `CommandLineToArgvW` |
| T-C Config | missing file, bad JSON, unknown field (`logEnabled`, `log.path`, `log.enabled`), invalid `log.level`, wrong types, bad roots (relative, `/`, invalid Windows), duplicate roots (case), Windows path in `executable`, `windowsExecutable` outside defaults, missing agent executable, `.cmd` in mode `windows`, `PATH` lookup found / not found, empty `projects` with mode `none`, `examples/agent-relay.json` valid (R42) |
| T-M Merge | distribution fallback, agent fields project-over-defaults taken whole (project agent `"env": {}` drops default agent env), env order defaults → project → agent, log fields defaults → project → agent (`dir` from defaults kept when project agent sets only `level`), `null` env value rejected, mapping concat + duplicates |
| T-S Selection | exact root, subdir, case, `app` vs `app-other`, nested longest wins, `C:\`, UNC, no match → each mode |
| T-P Mapping | both directions, custom mappings, longest prefix, `C:` → `C:\`, `/mnt/cat` vs `/mnt/c`, Linux case, `/` in Windows input, multi-line untouched, `café.txt` |
| T-W Walker | byte identity, whitespace, big number, escapes kept when untouched, minimal re-encoding, invalid JSON / trailing garbage / empty line → original |
| T-R Rules | unknown field with mapped path translated; path key translated, other keys not; path later in string untouched; `"C:\\app\\f.txt is missing"` rewritten whole; `file:///` untouched. Claude: `message.content[].input.file_path` translated, `input.content`/`new_string`/`text` untouched even if exactly a path, `{"content":{"path":…}}` untouched, `ShouldTranslate`. Codex: `cwd`, `cwds[]`, `projects` keys, `aggregatedOutput` untouched, `ShouldTranslate` |
| T-F Past failures | POC self-test vectors; `auth status --json` passthrough; distro unquoted; `runtimeWorkspaceRoots` vector not made worse; `WSL_UTF8=1` in launch env |
| T-A Args | `--relay-version`, `--relay-self-test`, unknown `--relay-x`, `--version` forwarded |
| T-L Launch spec | exact arguments for: no env, env, spaces in cwd, mode `wsl`, mode `windows`; masking in logged command line |

### 12.3 Lifecycle tests

Fake child: `agent-relay-tests.exe --fake-child <script>` — echo, echo Linux paths, write stderr,
exit N, crash, ignore stdin EOF, exit with stdin open, unterminated last line, 10 MB line, spawn a
grandchild immediately at start that holds stdout open.

Harness: in-process (`RelayMain.Run` with injected streams + `IChildLauncher`, no job) for most
cases; out-of-process (`agent-relay-tests.exe --relay-under-test <script>`, real launcher with job)
for job cases, so the test runner itself never joins a job.
Every test has a 10 s timeout.

Cases: stdin EOF closes child stdin; child ignoring EOF keeps relaying; exit with stdin open →
relay exits; exit codes 0, 3, -1; crash; start failure → exit 1 + message; stderr bytes identical;
stdout only protocol; 10 MB line intact; unterminated last line intact; passthrough delivers a
prompt without newline at once; **[win]** grandchild spawned at child start and holding stdout → relay
exits after child exit and grandchild is gone; **[win]** killing the relay kills the child (PID check); cleanup failure
(fake `IChildProcess` cleanup throws, grandchild holds stdout) → `cleanup failed` on stderr, relay
exits within 5 s + margin with the child's exit code; logging: each level writes exactly its
entries (`basic` has no `RECEIVED`/`FORWARDED`/`CHANGE` entries, `changes` adds only `CHANGE`
entries, `full` adds lines); `off` creates no file; new file
per session, pruning keeps `keep` files when none are in use, a file in use is skipped,
entry that would pass `maxSizeMB` → skipped, `LOG LIMIT` written, no more writes, file size ≤ cap
+ `LOG LIMIT` entry, log dir is a file → one warning,
relay continues.

### 12.4 Manual (README checklist)

T-L7 (decides §6.1 variant), on Windows with the real distro: `env A=1` visible, `--cd` honored,
args `$HOME;x`, `a b`, `it's`, `"q"`, `*`, `` `id` ``, `café` arrive literally.
Zed, both agents: start thread, run a tool, edit a file, approve a permission, resume, close Zed.

---

## 13. Documentation (V18, R40–R42)

README:
1. What it is / isn't (no sync, no isolation by itself).
2. Build (`build.cmd`, `build.cmd test`); install = copy `bin\agent-relay-*.exe` + `agent-relay.json`.
3. Config reference (incl. whole-value agent fields, D12) + migration from POC configs
   (`claudePath`/`codexPath` → `agents.<name>.executable`, `logEnabled`/`logPath` → `log`).
4. Isolation setup (`wsl.conf`, `fstab` drvfs, `wsl --shutdown`).
5. Agent install in WSL (Claude native installer; Codex install script, `bubblewrap` recommended).
6. Zed: `agent_servers` only in global `%APPDATA%\Zed\settings.json` (project settings drop it,
   zed-industries/zed#39011):
   ```json
   "agent_servers": {
     "claude-acp": {
       "type": "registry",
       "env": {
         "CLAUDE_CODE_EXECUTABLE": "C:\\Users\\<me>\\Tools\\agent-relay-claude.exe",
         "CLAUDE_CONFIG_DIR": "\\\\wsl.localhost\\<distro>\\home\\<user>\\.claude"
       }
     }
   }
   ```
   Codex entry: copied after verification (TODO.md). Known limitation: resumed Claude history
   shows Linux paths (D11). Mode `windows` inherits `CLAUDE_CONFIG_DIR` from Zed — explain.
7. Logging levels + privacy warning (`full`, and `basic`/`changes` partly) (R36).
8. Troubleshooting: `--relay-self-test`, `WSL_E_DISTRO_NOT_FOUND`, project not matched, logs.

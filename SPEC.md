# Agent Relay — Specification

Status: draft 4 (2026-11-03) — design decisions made (DEV-1, DEV-3 accepted; translation by
mapped-prefix path detection, R18/R19/R23 amended). Pending verification on Windows: DEV-2 command-line
form, opaque-field review, default Windows executable paths (see §16 and `TODO.md`).
Sources: `VISION.md` (Vn), `REQUIREMENTS.md` (Rn, Qn), and lessons from the proof-of-concept
wrappers `../claude-wsl` and `../codex-wsl` (marked **[POC]**).
Where this spec deviates from a requirement, the deviation is listed in §16 and marked **[DEV-n]**.

---

## 1. Overview

Agent Relay is a set of small Windows console programs that sit between an editor (Zed) running
natively on Windows and a coding agent (Claude Code, Codex) running inside an isolated WSL
distribution.

```
Zed (Windows)                agent-relay-<agent>.exe (Windows)            WSL distro
 agent adapter  ── stdin ──▶  select project → launch wsl.exe ── stdin ──▶  agent CLI
                ◀─ stdout ──  translate paths per line          ◀─ stdout ──  (Linux paths)
                ◀─ stderr ──  raw passthrough                  ◀─ stderr ──
```

The relay:
1. selects a project configuration from its Windows launch directory;
2. starts the agent through `wsl.exe` with an explicit Linux working directory;
3. relays newline-delimited JSON in both directions, translating string values that are entirely
   a path under a configured mapping, between Windows and Linux form;
4. passes the child's exit code through.

It never mounts, copies, or synchronizes files (V13, R35). Isolation is the user's WSL setup (V4).

### 1.1 Deliverables

| Artifact | Purpose |
|---|---|
| `agent-relay-claude.exe` | Relay for Claude Code (`CLAUDE_CODE_EXECUTABLE` replacement) |
| `agent-relay-codex.exe` | Relay for Codex (`codex app-server`) |
| `agent-relay-tests.exe` | Unit + lifecycle tests; also acts as the fake child process |
| `agent-relay.json` | Single config file, beside the executables |
| `build.cmd` | Windows build (csc only) |
| `build.sh` | Optional WSL test build with mono (Q1) — never used for releases |
| `README.md`, `examples/agent-relay.json` | Documentation |

---

## 2. Terminology

| Term | Meaning |
|---|---|
| Agent | A coding agent CLI (`claude`, `codex`). Identified by a lowercase name. |
| Agent name | `claude` or `codex`; fixed per executable; used in config keys, log names, messages. |
| Launch dir | `Environment.CurrentDirectory` of the relay process at start (Windows path). |
| Project | One entry of `projects[]`, selected by launch dir. |
| Effective config | Merge of `defaults` → project → project agent section (§5.3). |
| Mapping | A pair (`windows`, `linux`) of directory roots. |
| Rules | Per-agent class declaring opaque fields, `ShouldTranslate`, default Windows executable. |
| Mode | How the relay runs when no project matches: `wsl`, `windows`, `none` (§5.4). |
| Line | Bytes between `\n` (0x0A) separators on a protocol stream. |

---

## 3. Source layout and architecture (V14, R3, R5)

```
agent-relay/
  build.cmd
  build.sh                       optional, mono, tests only
  src/core/
    RelayMain.cs                 entry orchestration: args → config → resolve → launch → pump → exit
    RelayArgs.cs                 --relay-* flag parsing
    Config.cs                    JSON load, strict schema validation, merge
    ProjectResolver.cs           launch dir → ResolvedProject (project / default mode)
    PathMapper.cs                bidirectional longest-prefix mapping
    JsonPathWalker.cs            token-preserving JSON walker
    AgentRules.cs                abstract rules base class
    ArgQuoter.cs                 Windows command-line quoting (CommandLineToArgvW rules)
    LaunchSpec.cs                FileName, Arguments, WorkingDirectory, Environment
    ChildLauncher.cs             IChildLauncher / IChildProcess seam + Process implementation
    LinePump.cs                  protocol pump (line-framed, translating)
    RawPump.cs                   stderr pump (raw bytes)
    Logger.cs                    optional log, rotation, masking
    Version.cs                   version constant
  src/agents/claude/
    Program.cs                   Main → RelayMain.Run("claude", new ClaudeRules(), args)
    ClaudeRules.cs
  src/agents/codex/
    Program.cs
    CodexRules.cs
  tests/
    TestMain.cs                  runner; also --fake-child mode (§13.3)
    *Tests.cs
  examples/agent-relay.json
  README.md  VISION.md  REQUIREMENTS.md  SPEC.md
```

Rules:
- Core files are compiled into every exe (no shared DLL). Install = exe + `agent-relay.json`.
- The core never references an agent by name except through `AgentRules`.
- Adding an agent = new `src/agents/<name>/` (Program.cs + Rules class) + one line in `build.cmd`
  (+ `build.sh`). No core edits (R5).
- No plugin framework, no reflection-based discovery.
- Both POCs were ~90 % identical single files; their shared logic maps to core as follows:
  `QuoteArgument`→ArgQuoter, `SelectProject`/`LoadConfig`→ProjectResolver/Config,
  `TranslatePath`→PathMapper, Claude's `TranslateValue`→JsonPathWalker,
  `StartProtocolPump`/`StartPump`→LinePump/RawPump, `WriteLog`/`MaskSecret`→Logger.

### 3.1 AgentRules contract

```csharp
abstract class AgentRules
{
    public abstract string Name { get; }                      // "claude"
    public abstract string DefaultWindowsExecutable { get; }  // for mode "windows" (§5.4)
    // true → protocol translation enabled for this invocation; false → pure passthrough
    public abstract bool ShouldTranslate(string[] args);
    // member value (and everything under it) is never translated: free text, file content,
    // commands, diffs, tool output
    public abstract bool IsOpaqueField(string name, string parentName);
}
```

`parentName` is the member name of the enclosing object (or `""` at root / inside arrays the
array's member name is inherited, as in the POC).

---

## 4. Build (V15, V16, R1–R4, Q1)

### 4.1 build.cmd

- Compiler: `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`. Fail with a message if missing.
- Flags: `/nologo /langversion:5 /target:exe /optimize+ /reference:System.Web.Extensions.dll`.
- Outputs into `bin\`: `agent-relay-claude.exe`, `agent-relay-codex.exe`, `agent-relay-tests.exe`.
  Each compiled from `src\core\*.cs` + its own entry dir (tests: `src\core\*.cs`, all
  `src\agents\*\*Rules.cs`, `tests\*.cs`).
- Copies `examples\agent-relay.json` to `bin\` only if `bin\agent-relay.json` does not exist.
- Any compile failure → `exit /b 1` immediately. Success → `exit /b 0`.
- Optional `build.cmd test` → build, then run `bin\agent-relay-tests.exe`, propagate its exit code.

### 4.2 Language constraints (C# 5, .NET Framework 4.x)

Forbidden: string interpolation, `?.`, `nameof`, expression-bodied members, auto-property
initializers, `using static`, tuples, `out var`, `async`/`await` (allowed by C# 5 but not used —
threads + blocking I/O only, R29). Only framework assemblies; JSON via `System.Web.Extensions`
(used only for decoding/validation, never for re-serializing whole messages).

### 4.3 build.sh (optional, Q1)

`mcs -langversion:5 -r:System.Web.Extensions ...` producing the same three exes; for agents running
tests in WSL with mono. Final builds are manual on Windows. Tests that need `wsl.exe` do not exist
(the launcher seam replaces it), so the full suite runs under mono.

---

## 5. Configuration (V6, V7, R12–R16, Q2)

### 5.1 Location and loading

- File: `agent-relay.json` in `AppDomain.CurrentDomain.BaseDirectory` (beside the exe). No other
  source (no project-dir config, no env var override) (R12). **[POC]** confirmed: config must not
  come from the project dir.
- Read once at start, UTF-8 (BOM tolerated). Changes require relay restart.
- Parsed with `JavaScriptSerializer.DeserializeObject` into dictionaries, then validated by hand
  (strict schema). Unknown members are errors — this catches POC-era fields
  (`logEnabled`, `logPath`, `claudePath`, `codexPath`, `mode` on a project) with a clear message.

### 5.2 Schema

```jsonc
{
  "defaults": {                         // optional
    "mode": "none",                     // "wsl" | "windows" | "none"; default "none" (§5.4)
    "distribution": "Ubuntu",           // default for projects; required for mode "wsl"
    "automountRoot": "/mnt",            // only for mode "wsl"; default "/mnt"
    "pathMappings": [ { "windows": "D:\\Shared", "linux": "/shared" } ],
    "env": { "NAME": "value" },
    "log": { "enabled": false, "path": "agent-relay-<agent>.log", "maxSizeMB": 10 },
    "agents": {
      "claude": { "executable": "/home/me/.local/bin/claude", "env": {}, "log": {},
                  "windowsExecutable": "C:\\Users\\me\\.local\\bin\\claude.exe" },
      "codex":  { "executable": "/home/me/.local/bin/codex" }
    }
  },
  "projects": [                         // optional if defaults.mode is "wsl" or "windows"
    {
      "windowsRoot": "C:\\Users\\me\\src\\app",     // required
      "linuxRoot": "/home/me/src/app",              // required
      "distribution": "Ubuntu",                     // required unless in defaults
      "pathMappings": [],
      "env": {},
      "log": {},
      "agents": {
        "claude": { "executable": "/home/me/.local/bin/claude", "env": {}, "log": {} }
      }
    }
  ]
}
```

Field types:

| Field | Type | Notes |
|---|---|---|
| `mode` | string | `defaults` only. |
| `distribution` | string | Non-empty, no whitespace or quotes (WSL distro names). |
| `automountRoot` | string | Absolute Linux path ≠ `/`. `defaults` only. |
| `windowsRoot`, `pathMappings[].windows` | string | Drive-absolute (`C:\…`, `C:/…`) or UNC (`\\server\share…`). |
| `linuxRoot`, `pathMappings[].linux` | string | Absolute, ≠ `/`. |
| `env` | object string→string\|null | Names match `^[A-Za-z_][A-Za-z0-9_]*$`. `null` removes an inherited key. |
| `log.enabled` | bool | |
| `log.path` | string | Absolute Windows path, or relative to exe dir. Default `agent-relay-<agent>.log`. |
| `log.maxSizeMB` | number > 0 | Default 10. |
| `agents.<name>.executable` | string | Always an absolute Linux path (WSL launch). Allowed in `defaults.agents` and `projects[].agents`. |
| `agents.<name>.windowsExecutable` | string | Absolute Windows path to an `.exe` (`%VAR%` expanded). Allowed **only** in `defaults.agents`; used only by mode `windows`. |

### 5.3 Merge (effective config)

Order: built-in defaults → `defaults` → project → `project.agents.<name>` (agent wins over
project, project wins over defaults) (R13).

- Scalars: later non-null wins.
- `env`: merged per key; `null` value deletes the key.
- `log`: merged per field.
- `pathMappings`: concatenated (defaults first, then project). Duplicate Windows roots or duplicate
  Linux roots after normalization → error.
- `agents.<name>.executable`: from `project.agents.<name>`, else `defaults.agents.<name>`. It is
  always a Linux path, so a WSL project can never inherit a Windows executable.
- `agents.<name>.windowsExecutable` is never merged into projects; only mode `windows` reads it.
- `defaults.agents.<name>.env/log` apply between `defaults` and project level.

### 5.4 Default mode (Q2 answer)

Applies only when **no project matches** the launch dir. It does not affect matched projects.

| `defaults.mode` | Behavior |
|---|---|
| `"none"` (default, also when `defaults` is absent) | Do not run the agent. stderr: `agent-relay-<agent>: no project in <config> matches <launchDir> (defaults.mode is "none")`, exit 1. Same as POC behavior and R15/R16. |
| `"wsl"` | Run in WSL using automount mapping. Synthesized project: `linuxRoot` = `<automountRoot>/<drive-lowercase>/<rest with />`, e.g. `C:\Users\me\x` → `/mnt/c/Users/me/x`. All other fields (`distribution`, `pathMappings`, `env`, `log`, `agents`) come from `defaults`. Translation mapping = one mapping per drive letter `X:\` ↔ `<automountRoot>/x` plus `defaults.pathMappings` (longest prefix still wins). UNC launch dir → error, exit 1. **Requires automount enabled** — the README must state this mode trades V3 isolation for convenience. |
| `"windows"` | Run the agent natively on Windows, no WSL. Only `defaults.agents.<name>` (`windowsExecutable`, `env`, `log`) and `defaults.log`/`defaults.env` matter; other fields (including `executable`) are ignored. Executable: `defaults.agents.<name>.windowsExecutable`, else `rules.DefaultWindowsExecutable` — the agent's standard Windows install location (§8.3, §8.4), with `%VAR%` expanded; missing file → error naming the path and suggesting `defaults.agents.<name>.windowsExecutable`. No path translation (pure passthrough, like `ShouldTranslate=false`). |

Rationale and constraints for `windows` mode:
- **[POC]** `claude-wsl` native mode needed `cmd.exe` for npm `.cmd` shims; R6 forbids `cmd.exe`.
  Therefore only `.exe` targets are allowed; `.cmd`/`.bat` → config error naming the file and asking
  for the real `.exe` path.
- Guard: if the resolved executable is the relay itself (same full path, or file name starts with
  `agent-relay-`), error — prevents infinite self-launch.
- **[POC]** A Windows Claude stores sessions on the Windows side; if Zed sets
  `CLAUDE_CONFIG_DIR` to the WSL path, the Windows agent inherits it. The README must explain this.

**[DEV-1, accepted]** R6 ("no native mode") and R15 ("no `/mnt/<drive>` fallback") stay true for
matched projects; the Q2 answer adds opt-in fallbacks for unmatched launch dirs only.

### 5.5 Validation (R14) — all at start, before any child starts

1. File exists; valid JSON; root is an object.
2. Unknown members / wrong types → error with JSON path, e.g. `projects[1].logEnabled: unknown field`.
3. `projects` absent or empty is allowed only if `defaults.mode` is `wsl` or `windows`.
4. Each project: `windowsRoot`, `linuxRoot` valid (§5.2); effective `distribution` present.
5. Each mapping valid; normalized (Windows: `/`→`\`, trailing `\` trimmed except it keeps `C:`
   comparable as `C:`; Linux: trailing `/` trimmed).
6. No two projects with equal `windowsRoot` (case-insensitive) (R14).
7. Agent section: for the selected project (or mode `wsl`), an `executable` for **this** agent must
   resolve; missing → `agent-relay-claude: project C:\… has no agents.claude.executable`.
   Other agents' sections are validated for shape only.
8. Mode `windows`: resolved `windowsExecutable` exists (`File.Exists`) and ends with `.exe`.
9. `executable` that is not an absolute Linux path (e.g. `C:\…`) → error; `windowsExecutable`
   outside `defaults.agents` → error.

Structural errors anywhere in the file fail every invocation (a broken config should be noticed
immediately, not only when a certain project is opened).

### 5.6 Project selection (V7, R15)

1. `launchDir` = `Environment.CurrentDirectory`, normalized like `windowsRoot`.
2. Candidates: projects where `launchDir` equals `windowsRoot` or starts with `windowsRoot + "\"`
   (ordinal, case-insensitive). Drive roots: `C:` matches `C:\anything`.
3. Longest `windowsRoot` wins. Ties impossible (duplicates rejected).
4. No candidate → default mode (§5.4).

### 5.7 Error output (R16)

Every config/selection error: one line on stderr `agent-relay-<agent>: <message>`, exit code 1,
no child started, nothing on stdout. Messages include the config path when relevant.

---

## 6. Command line (R11)

- Relay-own flags have the `--relay-` prefix and are recognized **only as the first argument**:
  - `--relay-version` → stdout `agent-relay-<agent> <version>`, exit 0.
  - `--relay-self-test` → load + validate config, resolve project for the launch dir, print to stdout
    the selected project/mode, effective settings, the exact launch command line and the env
    overrides (masked, §11.3); run internal quick checks (quoting, mapping round-trip of the launch
    dir). Does not start the agent. Exit 0 if all OK, else 1.
  - `--relay-help` → usage, exit 0.
  - Any other `--relay-*` as first argument → error, exit 1 (typo protection).
- Everything else, including `--version`, `--help`, `-p`, is forwarded verbatim to the agent.

---

## 7. Launch (V1, V3, V8, R6–R10, R28)

### 7.1 WSL launch (matched project, or mode `wsl`)

```
FileName : %SystemRoot%\System32\wsl.exe
Arguments: -d <distro> --cd <linuxCwd> --exec env [K=V …] <agentExe> <arg1> … <argN>
WorkingDirectory: launch dir
```

- `linuxCwd` = launch dir mapped Windows→Linux through the effective mappings (always passed, R8).
  Unmappable launch dir is impossible for a matched project (it is under `windowsRoot`).
- `env K=V …` prefix only when effective `env` is non-empty; otherwise `--exec <agentExe> …`
  directly. No `WSLENV` (R10). Env values are passed as separate arguments (no shell).
- Each token is quoted with ArgQuoter (§7.3).
- The relay sets `WSL_UTF8=1` in the **wsl.exe process environment** (Windows side; not forwarded
  into Linux). **[POC]** Without it, `wsl.exe` writes its own errors (e.g. `WSL_E_DISTRO_NOT_FOUND`)
  as UTF-16LE to stdout, which the POC forwarded to Zed as garbage JSON lines.
- No `cmd.exe`, no native fallback, no Windows `PATH` lookups inside WSL (R6, R7). Works with
  `automount.enabled=false` and `interop.enabled=false`.

**[DEV-2, decided by test]** Two candidate command-line forms; the test T-L7 (§13.4) picks one:

| Variant | Command line | Argument safety |
|---|---|---|
| A (preferred) | `-d <d> --cd <cwd> --exec env [K=V…] <exe> <args…>` | No shell; args arrive literally. |
| B (R6 as written) | `-d <d> --cd <cwd> -- env [K=V…] <exe> <args…>` | If `--` goes through the default shell, every token is additionally POSIX single-quoted (`'…'`, `'` → `'\''`). |

Selection rule: use A if T-L7 passes with A (literal `$HOME;x`, `--cd` honored, env set);
otherwise B with single-quoting, and B must pass the same test. The form is built in one function
(`LaunchSpec.BuildWslArguments`), so switching is a one-place change. The chosen variant and the
Windows/WSL version it was verified on are recorded in the README; R6 is updated to match.

### 7.2 Windows launch (mode `windows`)

```
FileName : <resolved .exe>
Arguments: <arg1> … <argN>        (ArgQuoter)
WorkingDirectory: launch dir
Environment: inherited + effective env overrides (null removes)
```

### 7.3 Argument quoting (R9)

Implements the inverse of `CommandLineToArgvW` / MSVCRT parsing:
- Token without whitespace and `"` and non-empty → unquoted. **[POC]** Distribution names must stay
  unquoted: the failing Codex log shows `-d "DenwAP74"` producing `WSL_E_DISTRO_NOT_FOUND`;
  minimal quoting fixed it.
- Otherwise wrap in `"`; backslashes before `"` and before the closing quote are doubled; `"` →
  `\"`. Unicode passes as UTF-16 unchanged.
- Empty string → `""`.

### 7.4 Process setup and start failure

`UseShellExecute=false`, `CreateNoWindow=true`, redirect stdin/stdout/stderr, no encoding
conversion on streams (raw `BaseStream`s). If `Process.Start` throws → stderr
`agent-relay-<agent>: cannot start <FileName>: <message>`, log it, exit 1 (R28).

---

## 8. Path translation (V9, R17–R23)

### 8.1 PathMapper (R17)

Input: list of mappings (project root pair first, then `pathMappings`), direction.

Windows→Linux (`toLinux`):
1. Skip if value contains `\r` or `\n`, or doesn't look like an absolute Windows path
   (`^[A-Za-z]:[\\/]`, `^[A-Za-z]:$`, or `^\\\\`).
2. Normalize `/`→`\` for matching only.
3. Pick the mapping with the longest `windows` root such that value equals root or starts with
   `root + "\"` (ordinal, case-insensitive).
4. Result = `linux` + suffix with `\`→`/`.

Linux→Windows (`toWindows`):
1. Skip if value contains `\r` or `\n`, or does not start with `/`.
2. Longest `linux` root with value == root or startsWith `root + "/"` (ordinal, case-sensitive).
3. Result = `windows` + suffix with `/`→`\`; a bare drive result `C:` becomes `C:\`.

Unmatched → value unchanged (no `/mnt/<drive>` guessing except the explicit mode-`wsl` mappings).
Boundary rule guarantees `/mnt/cat` ≠ `/mnt/c` and `C:\app-other` ≠ `C:\app`.

Mappings must be unambiguous both ways for deterministic round-trips; overlapping roots are
allowed, longest wins.

### 8.2 JsonPathWalker (R18–R21)

A hand-written tokenizer over the decoded line (POC Claude design):
- Copies every token, whitespace and number **verbatim** from the input; only a translated string
  value is re-encoded. Untouched lines are byte-identical; numbers like `9007199254740993` keep
  exact digits (R20).
- String tokens are decoded (JavaScriptSerializer `Deserialize<string>` on the token, or own
  decoder), passed to the mapper, and re-encoded only if changed, using a minimal encoder that
  escapes `"`, `\` and U+0000–U+001F only. (JavaScriptSerializer.Serialize would turn `&`, `'`,
  `<`, `>` into `\u00XX`.)
- Context tracked per value: current member name, parent member name, opaque flag. Arrays inherit
  the member name of the array.
- **Mapped-prefix detection.** Every string value — and every object key — outside opaque members
  is offered to PathMapper. It is translated if the string *starts with* a mapped root followed by
  end-of-string or a separator (§8.1). This is a heuristic, not proof that the string is a path:
  - Strings that merely *contain* a path later in the text (`"Edit C:\\app\\x.cs"`, `file:///…`
    URIs, `cd /mnt/c/app && make`) never match.
  - Strings that *start with* a path followed by prose (`"C:\\app\\file.txt is missing"`) do
    match and are rewritten as a whole (`/mnt/c/…/file.txt is missing`), because paths may legally
    contain spaces and the relay cannot tell where the path ends.
  - No field list is needed, so new path fields added by future agent versions are translated
    automatically.
- **Keys** follow the same rule. **[DEV-3, accepted; R19 amended]** A key is translated only when it
  starts with a mapped root, i.e. it represents a path; there is no per-agent key declaration.
  Example: Codex sends `config.projects` keyed by project path
  (`{"C:\\…":{"trust_level":"trusted"}}`), and the trust setting must reach Codex under the Linux
  path.
- Opaque members (`rules.IsOpaqueField`) are copied verbatim with all descendants. This denylist
  protects content that must stay as written even when it starts with a mapped path: free text,
  file bodies, edit strings, commands, diffs, tool output.
- Diagnostics: with logging on, every translation records `field=<member name>` in the log, so new
  fields being translated are visible.
- Whole line must be exactly one JSON value plus whitespace; otherwise FormatException.

Why a denylist instead of an allowlist of path fields:
- An allowlist misses new path fields silently: paths leak to the editor in Linux form (e.g. files
  that won't open from the agent panel).
- A denylist misses new *content* fields: if an unknown content field holds a string starting with
  a mapped path, it is rewritten. This is not only cosmetic. In the editor→agent direction it can
  change data the agent acts on (e.g. text the agent later writes to a file); in the agent→editor
  direction it changes what the editor stores or sends back. This is a known gap in V9's
  preservation guarantee, accepted in exchange for automatic coverage of new path fields.
- Mitigation: opaque lists cover all content fields seen in captured traffic of each agent; the
  `field=` log diagnostics expose newly translated fields; reviewing them against a full captured
  session is a release check (`TODO.md`).
The Codex POC used this approach (all strings starting with a mapped root, no opaque list) and
worked in the captured sessions; the Claude POC used an allowlist.

Why mapped-prefix and not substring replacement (decision, 2026-11-03): replacing mapped paths
*anywhere inside* strings (`"Open file C:\\app\\x.cs"`) was considered and rejected:

1. **File corruption.** Edits travel as strings (`old_string`/`new_string`, diffs, file content,
   commands). If a file contains a mapped path, a rewritten `old_string` no longer matches (edit
   fails) and a rewritten `new_string` writes a Linux path into a Windows file.
2. **No reliable path end in free text.** Paths may contain spaces, dots, quotes and brackets
   (`"Open C:\\My Files\\a.txt and b.txt"`). The suffix must be converted too (`\` ↔ `/`); a wrong
   guess produces half-converted values like `/mnt/c/app\src\x.cs`.
3. **Streaming chunks.** Agent text arrives in deltas; one path can be split across two chunks
   (`"…/mnt/c/Us"` + `"ers/…"`), so translation would be inconsistent.
4. **User intent.** A prompt like "replace `C:\\app` with `D:\\data` in config.ini" means literal
   Windows paths; translating them makes the agent write Linux paths into the file.
5. **Lossy round trips.** `c:/path/file` → `/mnt/c/path/file` → `C:\\path\\file`; exact string
   comparisons (edit matching, permission rules) break.

Accepted consequence: paths inside chat text appear in Linux form in agent replies, and Windows
paths typed in prompts reach the agent unchanged (agents generally convert them themselves).
Structured fields (`cwd`, `file_path`, tool-call locations), which Zed uses to open and follow
files, are translated.

Possible future opt-in (not in initial scope): Linux→Windows rewriting of mapped paths only in
agent reply text shown to the user, never in the editor→agent direction and never in opaque
fields such as edits and commands. It would still suffer from point 3.

Error handling (R21): any exception → forward the **original** line bytes, log
`<direction> TRANSLATION ERROR: <message>`. Never drop or truncate a line.

### 8.3 Claude rules (R22)

`ShouldTranslate(args)`: true iff any argument contains `stream-json`
(`--output-format stream-json`, `--input-format stream-json`, `--output-format=stream-json`).
Otherwise passthrough. **[POC]** `claude auth status --json` etc. produced ~570 false
`TRANSLATION ERROR` entries in the POC log.

Path fields seen in the captured log (translated automatically, listed for tests only):
`cwd`, `original_cwd`, `path`, `file_path`, `filePath`, `transcript_path`, `persistedOutputPath`,
`user_output_styles_dir`. Paths under `~/.claude` are unmapped and therefore unchanged.

Opaque fields: `text`, `old_string`, `new_string`, `command`, `partial_json`, `stdout`, `stderr`,
`content` **except** when `parent == "message"` (so `message.content[]` blocks are walked and
`tool_use.input.file_path` is translated, but `tool_use.input.content` — file bodies — and
`tool_result.content` stay untouched).

`DefaultWindowsExecutable`: `%USERPROFILE%\.local\bin\claude.exe` (Claude Code native
installer location).

### 8.4 Codex rules (R23)

`ShouldTranslate(args)`: true iff `args` contains `app-server`. Otherwise passthrough
(`codex --version`, `codex login status`).

Path fields seen in captured `codex app-server` traffic (Zed 1.26.2, 2026-11-01; translated
automatically, listed for tests only): `cwd`, `cwds`, `path`, `runtimeWorkspaceRoots`, and the keys
of `projects`.

Opaque fields: `aggregatedOutput`, `text`, `delta`, `command`, `diff`, `unifiedDiff`, `content`,
`output`, `stdout`, `stderr`, `summary`, `message`, `instructions`, `baseInstructions`,
`developerInstructions`.

**[POC]** The Codex POC replaced every JSON string starting with a mapped root (kept, §8.2), but
also inside command output and text (now excluded by the opaque list), and had `--cd` commented
out. The captured log contains a `runtimeWorkspaceRoots` value `"/mnt/c/…/funspintest/C:\\Users\\…"`
sent *by Codex* (Codex→Zed, response to request id 5). It looks like a Windows path joined to a
Linux cwd, probably from state persisted during earlier POC runs; the log does not show its cause.
It is used as a test vector only: the relay must forward such a value without making it worse, and
correct launch (`--cd`) plus `cwd` translation must not produce new ones.

The opaque list is a starting point: review it against a captured session with tool calls,
edits and approvals (see TODO.md). It is data in `CodexRules.cs`; nothing else changes.

`DefaultWindowsExecutable`: npm global install location
`%APPDATA%\npm\node_modules\@openai\codex\vendor\x86_64-pc-windows-msvc\codex\codex.exe`
(the native binary behind the `codex.cmd` shim). To be verified on a Windows install (TODO.md).

---

## 9. Relay I/O (V10, R24–R26, R29)

Three threads, all blocking reads (idle CPU ≈ 0, R29):

| Pump | From → To | Framing | Translation |
|---|---|---|---|
| input | relay stdin → child stdin | `\n` lines | Windows→Linux |
| output | child stdout → relay stdout | `\n` lines | Linux→Windows |
| error | child stderr → relay stderr | raw bytes, 8 KB buffer | none (R25) |

When `ShouldTranslate(args)` is false (passthrough: `claude auth status`, `codex --version`,
interactive login prompts) or in mode `windows`, the input and output pumps are **raw byte
pumps** like the error pump: every read chunk is written and flushed immediately, so prompts
without a trailing newline are delivered at once. Line framing applies only when translating.

Line pump rules (translation enabled):
- Read raw bytes; split on 0x0A only; lines are unbounded (Codex `config/read` replies are large).
- A line is decoded as strict UTF-8. Invalid UTF-8 or translation disabled → forward the original
  bytes unchanged.
- Trailing `\r` before `\n` is preserved as part of the line (not normalized).
- Output = (translated or original) bytes + `\n`, then `Flush()` — one write+flush per line, no
  delay beyond one line (R24). No BOM ever written.
- At EOF, a final unterminated fragment is forwarded as-is without adding `\n`.
- stdout carries only child stdout lines (R26). All relay diagnostics go to stderr or the log.

---

## 10. Lifecycle and exit (V11, R27)

1. Start child inside a Windows **Job Object** with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`
   (P/Invoke, kernel32). If the relay process dies or is killed by the editor, Windows closes the
   job handle and terminates `wsl.exe` (or the Windows agent) — no orphaned children. Start the
   three pumps. Input pump is a background thread.
2. Relay stdin EOF → close child stdin. The relay does **not** kill the child on EOF: agents may
   legitimately keep working after stdin EOF (e.g. a prompt piped to `claude -p` streams output for
   minutes). A child that ignores EOF keeps running until it exits or the editor terminates the
   relay; in the latter case the job object (step 1) terminates the child. The relay itself adds
   no wait that the editor cannot end.
3. Wait for child exit, then join output and error pumps with a **drain timeout of 2 s**. A
   descendant process that inherited the pipes can keep them open after the child exits; after the
   timeout the relay stops waiting, logs `drain timeout`, flushes stdout and exits.
4. Exit with the child's exit code (including negative/large codes such as wsl.exe `-1`).
5. Child exits while relay stdin is still open (Zed keeps it open) → do not wait for the input
   pump; process exits (background thread) — no hang.
6. Writing to child stdin after child exit (broken pipe) → input pump stops quietly, logs once.
7. Relay stdout closed by parent (broken pipe) → output pump keeps draining child stdout to avoid
   child blocking, discards, logs once.
8. Ctrl+C / console close: relay does not intercept; Windows delivers the event to the console
   group, wsl.exe forwards termination.
9. To verify on Windows (`TODO.md`): when the job object terminates `wsl.exe`, the Linux agent
   process also ends (WSL should tear down the session's processes when the `wsl.exe` client goes
   away).

---

## 11. Logging (V12, R30–R34, R36)

### 11.1 Config

Off by default. Effective `log` from §5.3. Path relative → beside the exe; default file name
`agent-relay-<agent>.log`. Parent directory created on first write.

### 11.2 Format (R31)

UTF-8 (no BOM), one entry per event:

```
[2026-11-03T20:45:01.1234567+01:00 session=<32-hex> <DIRECTION>]
<payload>
```

Directions: `LAUNCH`, `EDITOR -> AGENT RECEIVED`, `EDITOR -> AGENT FORWARDED`,
`AGENT -> EDITOR RECEIVED`, `AGENT -> EDITOR FORWARDED`, `AGENT STDERR`, `TRANSLATION ERROR`,
`ERROR`, `EOF`, `EXIT`. When the forwarded line equals the received line, the forwarded entry is
written as `(unchanged)` to halve log volume (POC log reached 13 MB).

### 11.3 Launch entry (R32)

Launch dir, selected project root or mode, relay version, agent args (one per line, escaped),
`FileName` + full `Arguments`, effective env overrides, and the relay's inherited environment.
Values of names containing `KEY`, `TOKEN`, `SECRET`, `PASSWORD`, `AUTH`, `CREDENTIAL`
(case-insensitive) → `<masked, N chars>` — also inside the `env K=V` tokens of the logged
command line.

### 11.4 Rotation and concurrency (R33)

- Before each write: if file size > `maxSizeMB` → replace `<log>.1` with the current file, start new.
- Several relays may run concurrently (Zed starts one per thread/agent). Writes and rotation are
  serialized across processes by a named mutex `Local\agent-relay-log-<hash of full log path>`;
  file opened with `FileShare.ReadWrite | Delete`, append, closed after each entry.

### 11.5 Failures (R34)

Any log failure → one stderr warning per process
(`agent-relay-<agent>: log disabled: <message>`), logging disabled for the rest of the process,
relay continues.

---

## 12. Security and isolation (V4, V13, R35, R36)

- The relay never mounts, copies, syncs, or changes permissions. Mappings only rewrite strings.
- Path translation is not a security boundary: the agent can access whatever its WSL distro can.
- Recommended WSL setup (README): `/etc/wsl.conf`
  `[automount] enabled=false`, `[interop] enabled=false appendWindowsPath=false`;
  explicit project mounts via `/etc/fstab` drvfs entries
  (`C:\\Users\\me\\src\\app /home/me/src/app drvfs defaults,uid=1000,gid=1000 0 0`) or Linux-native
  checkouts.
- Mode `wsl` requires automount (exposes all drives) and mode `windows` has no isolation at all; the
  README marks both as non-isolated convenience modes.
- Logs contain full conversation content, file contents and possibly secrets typed in prompts
  (R36); env masking covers only environment variables.

---

## 13. Tests (V17, R37–R39)

### 13.1 Runner

`agent-relay-tests.exe` — no dependencies. Minimal assertion helpers; each test is a named static
method registered in a list. Prints `PASS/FAIL <name>` per test and a summary; failures with
expected/actual. Exit 0 all pass, 1 otherwise (R37). `agent-relay-tests.exe <filter>` runs
matching tests.

### 13.2 Unit tests (R38)

| ID | Area | Cases |
|---|---|---|
| T-Q | Quoting | simple, empty, spaces, `"`, trailing `\`, `\"` combos, Unicode, distro unquoted; round-trip through a `CommandLineToArgvW` P/Invoke on Windows (skipped under mono) |
| T-C | Config | Windows path in `executable` rejected, `windowsExecutable` outside `defaults.agents` rejected, missing file, bad JSON, unknown field (`logEnabled`), wrong types, relative/`/` linux roots, invalid windows roots, duplicate roots (case), missing agent executable, `.cmd` in windows mode, default Windows executable expansion + missing-file error, `projects` empty with mode none |
| T-M | Merge | defaults→project→agent precedence, env null removal, log field merge, mapping concat + duplicates |
| T-S | Selection | exact root, subdir, case-insensitive, `app` vs `app-other`, nested projects longest wins, drive root `C:\`, UNC, no match → each mode |
| T-P | Mapping | both directions, custom mappings, longest prefix, `C:` root → `C:\`, `/mnt/cat` vs `/mnt/c`, Linux case-sensitivity, `/` separators in Windows input, multi-line strings untouched, Unicode (`café.txt`) |
| T-W | Walker | byte identity for untouched lines, whitespace preserved, `9007199254740993` exact, escapes (`\u00e9`, `\/`) preserved when untouched, minimal re-encoding, invalid JSON → original line, trailing garbage, empty line |
| T-R | Rules | Mapped-prefix detection: unknown field `"someNewField":"C:\\app\\x"` translated; key starting with a mapped path translated, other keys untouched; path later in a string untouched; `"C:\\app\\f.txt is missing"` rewritten as a whole (documented heuristic); `file:///` URI untouched. Claude: `message.content[].input.file_path` translated, `input.content`/`new_string`/`text` untouched even when exactly a path, `{"content":{"path":…}}` untouched, `ShouldTranslate` with/without `stream-json`. Codex: `cwd`, `cwds[]`, `projects` keys, `aggregatedOutput` untouched, `ShouldTranslate` |
| T-F | Past failures | POC self-test vectors (both); `auth status --json` passthrough; `-d "DenwAP74"` regression (distro unquoted); `runtimeWorkspaceRoots` corrupted-path case never produced by translation; wsl.exe UTF-16 error text not emitted as protocol (WSL_UTF8 set in LaunchSpec env) |
| T-A | Args | `--relay-version`, `--relay-self-test`, unknown `--relay-x` error, `--version` forwarded |
| T-L* | Launch spec | exact `wsl.exe` argument string for: no env, env, spaces in cwd, mode wsl, mode windows; masking in logged command line |

### 13.3 Lifecycle tests (R39)

The tests exe doubles as the fake child: `agent-relay-tests.exe --fake-child <script>` where the
script selects behavior: echo lines, echo with Linux paths, write stderr, exit with code N, crash
(unhandled exception), ignore stdin EOF, exit while stdin open, emit unterminated last line,
emit large (10 MB) line. Tests run `RelayMain` in-process with an `IChildLauncher` that starts the
fake child instead of `wsl.exe`, using pipes for relay stdin/stdout/stderr. Every test has a 10 s
timeout; a timeout fails the test (no hang).

Cases: EOF editor→agent closes child stdin; child ignores stdin EOF → relay keeps relaying while the
child runs, and terminating the relay process terminates the child (job object; checked by PID);
child exits but a grandchild keeps stdout open → relay exits after the drain timeout; passthrough
mode delivers a prompt without newline immediately; child exit with stdin open → relay exits; exit code
pass-through (0, 3, -1); child crash; start failure (nonexistent exe) → exit 1 + stderr message;
stderr bytes identical; stdout contains only protocol lines; large line intact; unterminated last
line intact; log failure (log path is a directory) → one warning, relay continues.

### 13.4 Manual verification (README checklist)

T-L7 (decides DEV-2): on Windows with the real distro, for variant A and then variant B (§7.1):
`env A=1` is visible to the child, `--cd <dir>` is the child's cwd, and args `$HOME;x`, `a b`,
`it's`, `"q"`, `*`, `` `id` ``, `café` arrive literally (check with a tiny script printing argv). Zed: start thread, run a tool, edit a file,
approve a permission, resume a thread, close Zed — for both agents.

---

## 14. Documentation (V18, R40–R42)

README sections:
1. What it is / isn't (no sync, no isolation by itself).
2. Build (`build.cmd`, `build.cmd test`), install (copy `bin\agent-relay-*.exe` +
   `agent-relay.json` to e.g. `C:\Users\<me>\Tools`).
3. Config reference (§5 condensed) + migration from `claude-wsl.json` / `codex-wsl.json`
   (`claudePath`/`codexPath` → `agents.<name>.executable`, `logEnabled`/`logPath` → `log{}`).
4. Isolation prerequisites (`wsl.conf`, `fstab` drvfs mounts, `wsl --shutdown` after changes).
5. Agent install in WSL (Claude: native installer to `~/.local/bin/claude`; Codex:
   `curl -fsSL https://chatgpt.com/codex/install.sh | sh`, `bubblewrap` package recommended — POC
   log shows the warning).
6. Zed setup (R41): `agent_servers` only in global `%APPDATA%\Zed\settings.json` (project
   `.zed/settings.json` silently drops it, zed-industries/zed#39011):
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
   `CLAUDE_CONFIG_DIR` is needed because Zed's Claude adapter reads session `.jsonl` history
   directly from disk on the Windows side; resumed history therefore shows Linux paths in old tool
   calls. Codex setting: documented after verification (relay executable as the Codex command with
   `app-server` arg).
7. Logging and its privacy warning (R36).
8. Troubleshooting: `--relay-self-test`, `WSL_E_DISTRO_NOT_FOUND`, project not matched, logs.

`examples/agent-relay.json` must pass validation; a unit test loads it (R42).

---

## 15. Migration from the POCs

| POC | Agent Relay |
|---|---|
| `claude-wsl.exe`, `codex-wsl.exe` | `agent-relay-claude.exe`, `agent-relay-codex.exe` |
| `<name>.json` per tool | one `agent-relay.json` with `agents{}` |
| `--self-test` | `--relay-self-test` (unit tests moved to `agent-relay-tests.exe`) |
| Claude `mode: native` per project | removed; only `defaults.mode: "windows"` for unmatched dirs, `.exe` only |
| Codex blanket string replacement | rules-based walker |
| Codex `--cd` commented out | always `--cd` |
| `StreamReader.ReadLine` (splits on `\r`, replaces bad UTF-8) | byte-level `\n` splitting |
| Logging on in sample config | off by default, rotation |
| `File.AppendAllText` per entry, per-process lock | named mutex, rotation |

---

## 16. Deviations and open items

| ID | Item | Status |
|---|---|---|
| DEV-1 | Q2 default modes `wsl` (automount mapping) and `windows` (native) relax R6/R15 for unmatched launch dirs only. | Accepted. |
| DEV-2 | `--exec` (variant A) vs `--` + single-quoting (variant B) in the wsl.exe command line (§7.1). | Decided by test T-L7; R6 updated afterwards. |
| DEV-3 | Object keys that represent paths (key starts with a mapped root, e.g. Codex `projects` keys) are translated; no per-agent key declaration. | Accepted; R19 amended. |
| OI-1 | Path detection by field list vs by value. | Decided: mapped-prefix detection + opaque denylist (§8.2); R18/R19/R23 amended. **Pending:** opaque lists reviewed against a full captured session (TODO.md). |
| OI-2 | Zed `agent_servers` entry for Codex. | Decided: configured in global Zed `settings.json`, works with the POC. **Pending:** copy into README (TODO.md). |
| OI-3 | Mode `windows` default executable. | Decided: each agent's standard Windows install path, override `windowsExecutable` (§5.4, §8.3, §8.4). **Pending:** verify paths on Windows (TODO.md). |
| OI-4 | Resumed Claude history shows Linux paths (adapter reads `.jsonl` directly). Translating transcripts would require writing files — out of scope (V13). | Accepted limitation; README note in TODO.md. |

---

## 17. Traceability

| Req | Section | Req | Section | Req | Section |
|---|---|---|---|---|---|
| R1 | 4.1 | R15 | 5.6, 5.4 | R29 | 9 |
| R2 | 4.2 | R16 | 5.7 | R30 | 11.1 |
| R3 | 3 | R17 | 8.1 | R31 | 11.2 |
| R4 | 4.1 | R18 | 3.1, 8.2 | R32 | 11.3 |
| R5 | 3 | R19 | 8.2 | R33 | 11.4 |
| R6 | 7.1 (DEV-1, DEV-2) | R20 | 8.2 | R34 | 11.5 |
| R7 | 7.1 | R21 | 8.2 | R35 | 12 |
| R8 | 7.1 | R22 | 8.3 | R36 | 12, 14 |
| R9 | 7.3 | R23 | 8.4 | R37 | 13.1 |
| R10 | 7.1 | R24 | 9 | R38 | 13.2 |
| R11 | 6 | R25 | 9 | R39 | 13.3 |
| R12 | 5.1 | R26 | 9 | R40 | 14 |
| R13 | 5.2, 5.3 | R27 | 10 | R41 | 14 |
| R14 | 5.5 | R28 | 7.4 | R42 | 14 |
| Q1 | 4.3 | Q2 | 5.4 | | |

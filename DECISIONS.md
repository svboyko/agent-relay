# Decisions

Why the spec is the way it is. Rules live in `SPEC.md`; this file holds the reasons.
**[POC]** = lesson from `../claude-wsl` / `../codex-wsl`.

## D1. Translate strings that start with a mapped path, never substrings
A string value or key is translated only if it *starts with* a mapped root followed by end or a
separator. Replacing paths *inside* strings was rejected:
- Edits (`old_string`, `new_string`, diffs, file bodies, commands) would be corrupted: edits stop
  matching, Linux paths get written into Windows files.
- Free text has no reliable path end (paths contain spaces, dots, brackets); wrong guesses give
  half-converted values like `/mnt/c/app\src\x.cs`.
- Streaming deltas can split one path across two chunks.
- Prompts like "replace `C:\app` with `D:\data` in config.ini" mean literal Windows paths.
- Round trips are lossy (`c:/x` → `/mnt/c/x` → `C:\x`), breaking exact comparisons.

Consequence: paths inside chat text appear in Linux form; Windows paths typed in prompts reach the
agent unchanged. Structured fields Zed uses to open files (`cwd`, `file_path`, tool locations) are
translated. A string that starts with a path followed by prose is rewritten as a whole.

Possible later opt-in: Linux→Windows rewriting in agent reply text only (still hit by split deltas).

## D2. Opaque denylist instead of path-field allowlist
- An allowlist silently misses new path fields → files that won't open from the agent panel.
- A denylist misses new *content* fields → a content string starting with a mapped path gets
  rewritten (a known gap in V9).
- Chosen: denylist, because new path fields then work automatically. Mitigation: opaque lists built
  from captured traffic, `field=` log diagnostics, review of a full session before release.
- **[POC]** Codex POC translated every string starting with a mapped root and worked; Claude POC used
  an allowlist.

## D3. Path-like object keys are translated
Codex sends `config.projects` keyed by project path; the trust setting must reach Codex under the
Linux path. Keys follow the same start-with-mapped-root rule; no per-agent key list. (R19)

## D4. Default modes for unmatched launch dirs only
Q2 answer: `defaults.mode` = `none` | `wsl` | `windows`. Matched projects always use WSL with explicit
mappings (R6, R15). `wsl` (automount) and `windows` (native) are convenience modes without
isolation; the README says so.
- `windows` launches only `.exe` files. **[POC]** npm `.cmd` shims needed `cmd.exe`, which R6 forbids.
- Default Windows executable = `<agent>.exe` found on `PATH`; otherwise set `windowsExecutable`.
  Avoids hardcoded install paths that must be verified per agent.

## D5. Child exit → kill job members → plain join
The relay assigns **itself** to a Job Object (kill on job close) before starting the child. Windows
puts processes created by a job member into the job, so the child and its descendants can never
escape (assigning the child after start would leave a window). The alternative, `CreateProcess`
suspended + assign + resume, would replace `System.Diagnostics.Process` with much more P/Invoke code.
When the child exits, the relay kills all other job processes, so descendants that inherited
stdout/stderr die and the pipes close. Pumps then reach EOF and are joined normally — no idle
timeout, no partial-line special cases.
Job setup failure → exit 1 before any child exists; running without a job could hang on join.
Trade-off: background processes started by the agent end with it.
Under WSL the pipes are held by `wsl.exe`, so stuck pipes mostly affect mode `windows`.

## D6. No kill on stdin EOF
Agents may keep working after stdin EOF (`claude -p` streaming for minutes). The child ends on its
own, or when the editor kills the relay (job object).

## D7. Line framing only when translating
Passthrough (`claude auth status`, `codex --version`, login prompts) and mode `windows` use raw byte
pumps, so prompts without a trailing newline appear at once.
**[POC]** `StreamReader.ReadLine` split on `\r` and replaced bad UTF-8 → byte-level `\n` splitting.
**[POC]** `claude auth status --json` produced ~570 false `TRANSLATION ERROR` entries →
`ShouldTranslate`.

## D8. Logs: per agent, one file per session
Log *settings* (`dir`, `enabled`, …) can be set in defaults, project and agent; there is no
project-wide or shared log *file*. Each relay process writes its own file
(`agent-relay-<agent>-<time>-<session8>.log`) and old files are pruned by count. No cross-process mutex, no rotation races.
- Pruning is best effort: files of running sessions can't be deleted, so with many concurrent
  sessions more than `keep` files exist.
- A per-file size cap (`maxSizeMB`) stops logging for that session, so one long session can't grow
  without limit. The log tail is lost then; raise the cap for full captures.
**[POC]** Shared log reached 13 MB; logging was on in the sample config → off by default.

## D9. Config only beside the exe, strict schema
**[POC]** Config must not come from the project dir. Unknown fields are errors, which catches
POC-era fields (`logEnabled`, `claudePath`, …) with a clear message.

## D10. Launch details from POC failures
- **[POC]** `-d "DenwAP74"` → `WSL_E_DISTRO_NOT_FOUND`: quote only when needed.
- **[POC]** `wsl.exe` writes its own errors as UTF-16LE to stdout → set `WSL_UTF8=1` for `wsl.exe`.
- **[POC]** Codex POC had `--cd` commented out → always pass `--cd`.
- **[POC]** Captured Codex log has `runtimeWorkspaceRoots` = `/mnt/c/…/C:\Users\…` (cause unknown,
  likely stale state). Kept as a test vector: the relay must not make it worse.

## D11. Known limitation: resumed Claude history
Zed's Claude adapter reads `<session>.jsonl` from `CLAUDE_CONFIG_DIR` directly, not through the
relay, so old tool calls show Linux paths. Fixing it would mean rewriting files (out of scope, V13).

## D12. Agent fields replace, not merge
Inside an agent section, `executable`, `env` and `log` are taken whole: project agent value, else
default agent value. The result then joins the chain defaults → project → agent, merged by `env` key
and by `log` field. Reason: one simple rule, no `null` deletion.
Consequences (documented in README):
- project `"agents": {"claude": {"env": {}}}` drops all `defaults.agents.claude.env` keys;
- a project agent `log` hides `defaults.agents.<name>.log` (but `defaults.log` and `project.log` still apply);
- an inherited env key can be overridden but not removed.

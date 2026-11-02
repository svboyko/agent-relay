# Requirements

## Purpose
- Keep editor/files on Windows; run coding agents in an isolated WSL environment.
- Connect local Zed to WSL agents without Zed remote/WSL mode; minimize cross-filesystem traffic, CPU load, and fan activity.
- Support WSL with Windows drive automount and Windows executable interop disabled. Never depend on either; launching `wsl.exe` from Windows is allowed.
- Isolation comes from WSL configuration and filesystem access controls; path translation alone is not a security boundary.

## Behavior
- Initial tools: `agent-relay-codex.exe`, `agent-relay-claude.exe`.
- Common config beside executables; per-project agent executable, WSL distribution, Linux directory, path mappings, environment overrides, logging.
- Select project by Windows launch directory; longest directory-prefix match. Fail clearly if unmatched or invalid.
- Set agent working directory explicitly. Support custom Linux paths; no `/mnt/c` assumption.
- Translate configured paths both ways using agent-aware rules. Preserve message text, file content, IDs, and unrelated fields.
- Relay protocol traffic promptly; preserve framing and Unicode. Keep diagnostics off protocol stdout.
- Support startup, resume, tools, permission requests, shutdown, and child exit codes. Avoid hangs on EOF or failure.
- Optional timestamped logs: direction, received/forwarded messages, launch details, errors. Logging failure must not stop relay.
- File access must be provisioned explicitly. Mapping does not mount, copy, or synchronize files; no automatic synchronization in initial scope.

## Architecture and build
- Simplicity first: shared core, thin agent entry points, explicit protocol rules. Add agents/editors without duplicating core code; no plugin framework initially.
- Build on Windows using its bundled .NET Framework C# compiler (`csc.exe`); no SDK, NuGet downloads, or extra build-tool installation.
- Use compatible C# syntax and framework assemblies. Provide simple build commands; solution/project files optional. No Native AOT requirement.
- Tests/self-tests need no extra installation. Cover quoting, project selection, custom mappings, content preservation, protocol lifecycle, and past failures.
- README: build, config, isolation prerequisites, file-access setup, and verified Zed/agent-specific settings. Include sample config.

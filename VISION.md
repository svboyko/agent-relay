# Vision

## Purpose
- **V1:** Keep editor/files on Windows; run coding agents in an isolated WSL environment.
- **V2:** Connect local Zed to WSL agents without Zed remote/WSL mode; minimize cross-filesystem traffic that does CPU load and fan activity.
- **V3:** Support WSL with Windows drive automount and Windows executable interop disabled. Never depend on either; launching `wsl.exe` from Windows is allowed.
- **V4:** Isolation comes from WSL configuration and filesystem access controls; path translation alone is not a security boundary.

## Behavior
- **V5:** Initial tools: `agent-relay-codex.exe`, `agent-relay-claude.exe`.
- **V6:** Common config beside executables; per-project agent executable, WSL distribution, Linux directory, path mappings, environment overrides, logging.
- **V7:** Select project by Windows launch directory; longest directory-prefix match. Fail clearly if unmatched or invalid.
- **V8:** Set agent working directory explicitly. Support custom Linux paths; no `/mnt/c` assumption.
- **V9:** Translate configured paths both ways using agent-aware rules. Preserve message text, file content, IDs, and unrelated fields.
- **V10:** Relay protocol traffic promptly; preserve framing and Unicode. Keep diagnostics off protocol stdout.
- **V11:** Support startup, resume, tools, permission requests, shutdown, and child exit codes. Avoid hangs on EOF or failure.
- **V12:** Optional timestamped logs: direction, received/forwarded messages, launch details, errors. Logging failure must not stop relay.
- **V13:** File access must be provisioned explicitly. Mapping does not mount, copy, or synchronize files; no automatic synchronization in initial scope.

## Architecture and build
- **V14:** Simplicity first: shared core, thin agent entry points, explicit protocol rules. Add agents/editors without duplicating core code; no plugin framework initially.
- **V15:** Build on Windows using its bundled .NET Framework C# compiler (`csc.exe`); no SDK, NuGet downloads, or extra build-tool installation.
- **V16:** Use compatible **V15** C# syntax and framework assemblies. Provide simple build commands; solution/project files optional. No Native AOT requirement.
- **V17:** Tests/self-tests need no extra installation. Cover quoting, project selection, custom mappings, content preservation, protocol lifecycle, and past failures.
- **V18:** README: build, config, isolation prerequisites, file-access setup, and verified Zed/agent-specific settings. Include sample config.

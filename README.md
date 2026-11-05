# Agent Relay

Run Claude Code and Codex in WSL while keeping your editor and tools on Windows.

Agent Relay connects your editor to agents running in WSL and translates paths between Windows and Linux.

Why use Agent Relay? Read [the story behind it](STORY.md).

Currently tested only with Zed. VS Code integration is planned.

## Build

Run in PowerShell from the project directory:

```powershell
.\build.cmd
```

Uses the .NET Framework compiler included with Windows. No additional SDK or build tools are needed. Output goes into `bin`.

To build and run tests:

```powershell
.\build.cmd test
```

## Install

Copy these files from `bin` to a directory of your choice, such as `C:\Tools\agent-relay`:

- `agent-relay-claude.exe`
- `agent-relay-codex.exe`
- `agent-relay.json`

You can keep only the executable for the agent you use.

Install and authenticate the corresponding agents inside WSL. Your project files must be accessible from both Windows and WSL.

Agent Relay does not mount or synchronize files. Isolation depends on your WSL configuration.

## Configure

Edit `agent-relay.json` beside the executables:

```json
{
  "defaults": {
    "distribution": "Ubuntu",
    "agents": {
      "claude": {
        "executable": "/home/me/.local/bin/claude"
      },
      "codex": {
        "executable": "/home/me/.local/bin/codex"
      }
    }
  },
  "projects": [
    {
      "windowsRoot": "C:\\Users\\me\\src\\app",
      "linuxRoot": "/mnt/c/Users/me/src/app"
    }
  ]
}
```

Replace the distribution, username, and paths with your own. The Windows and Linux project paths must refer to the same files.

Add more entries to `projects` as needed. Each project can use a different WSL distribution and agent settings.

## Use

### Zed

Install the Claude and/or Codex agents from Zed’s ACP Registry.

Merge the following into Zed’s global settings at `%APPDATA%\Zed\settings.json`:

```json
{
  "agent_servers": {
    "claude-acp": {
      "type": "registry",
      "env": {
        "CLAUDE_CODE_EXECUTABLE": "C:\\Tools\\agent-relay\\agent-relay-claude.exe",
        "CLAUDE_CONFIG_DIR": "\\\\wsl.localhost\\Ubuntu\\home\\me\\.claude"
      }
    },
    "codex-acp": {
      "type": "registry",
      "env": {
        "CODEX_PATH": "C:\\Tools\\agent-relay\\agent-relay-codex.exe"
      }
    }
  }
}
```

Adjust the paths and keep the entries for the agents you use. These settings belong in the global configuration, not `.zed/settings.json`.

Open your Windows project folder in Zed and start a Claude or Codex thread from the agent panel.

`CLAUDE_CONFIG_DIR` lets Zed read Claude’s session history from WSL. Resumed Claude threads may still show Linux paths in old tool calls. If you use the Windows fallback mode, Claude inherits this setting too.

The combined configuration above still needs an end-to-end check with the relay; see [TODO](TODO.md).

## Configuration file reference

Agent Relay reads `agent-relay.json` beside its executable at startup. Restart the agent after changing it.

The file contains two sections:

| Section | Description |
|---|---|
| `defaults` | Shared settings and behavior for unmatched projects. Optional. |
| `projects` | Project configurations. Required unless `defaults.mode` is `wsl` or `windows`. |

Use JSON with string values where indicated. Unknown fields and invalid values cause an error.

### Shared settings

These fields are allowed in both `defaults` and each project:

| Field | Description |
|---|---|
| `distribution` | WSL distribution name, without whitespace or quotes. Required for WSL launches, either here or inherited from `defaults`. |
| `agents` | Agent settings, keyed by `claude` or `codex`. |
| `pathMappings` | Additional Windows/Linux directory pairs. Default: `[]`. |
| `env` | Environment variables as string key/value pairs. Default: `{}`. |
| `log` | Logging settings. Logging is off by default. |

### Projects

Each project requires:

| Field | Description |
|---|---|
| `windowsRoot` | Absolute Windows project directory, using a drive letter or UNC path. |
| `linuxRoot` | Corresponding absolute Linux directory. Cannot be `/`. |

The relay selects a project from its Windows working directory. Subdirectories match too; the longest matching root wins. Windows matching ignores case.

The root pair automatically becomes a path mapping. Duplicate Windows project roots are rejected.

### Default modes

`defaults.mode` controls what happens when no project matches:

| Value | Behavior |
|---|---|
| `none` | Stop with an error. This is the default. |
| `wsl` | Use Windows drives mounted in WSL, such as `C:\src` → `/mnt/c/src`. Requires `distribution` and the agent’s `executable` in `defaults`. |
| `windows` | Run the agent on Windows, without path translation. |

A matching project always runs in WSL, regardless of the default mode.

`defaults.automountRoot` changes the mount prefix used by mode `wsl`. It defaults to `/mnt` and must be an absolute Linux path other than `/`. UNC working directories are not supported in this mode.

Mode `wsl` requires the corresponding drive mounts to exist. Neither fallback mode provides isolation.

### Agents

Both `agents.claude` and `agents.codex` accept:

| Field | Description |
|---|---|
| `executable` | Absolute Linux path to the agent executable. Required for that agent when running in WSL. |
| `env` | Environment variables for this agent. |
| `log` | Logging settings for this agent. |
| `windowsExecutable` | Absolute path to the Windows `.exe`, used only by mode `windows`. Allowed only under `defaults.agents`. Supports `%VARIABLE%` expansion. |

If `windowsExecutable` is omitted, the relay searches Windows `PATH` for `claude.exe` or `codex.exe`. The target must be a real `.exe`, not a `.cmd` script or another relay.

### Path mappings

Use `pathMappings` for directories outside the project root:

```json
{
  "pathMappings": [
    {
      "windows": "D:\\Shared",
      "linux": "/shared"
    }
  ]
}
```

Both fields are required. Windows paths must be absolute drive or UNC paths; Linux paths must be absolute and cannot be `/`.

Mappings work in both directions. The longest matching directory prefix wins. Windows matching ignores case; Linux matching is case-sensitive. Unmatched paths stay unchanged.

Default and project mappings are combined with the project root mapping. Duplicate Windows or Linux mapping roots are rejected.

Mappings only translate paths; they do not grant access to files.

### Environment variables

```json
{
  "env": {
    "MY_VARIABLE": "value"
  }
}
```

Values must be strings. Names may contain ASCII letters, digits, and underscores, and cannot begin with a digit.

For WSL launches, these variables are passed to the Linux agent. For Windows launches, they override inherited environment variables.

### Logging

```json
{
  "log": {
    "level": "off",
    "dir": "logs",
    "keep": 20,
    "maxSizeMB": 10
  }
}
```

| Field | Description |
|---|---|
| `level` | `off`, `basic`, `changes`, or `full`. Default: `off`. |
| `dir` | Windows log directory. Relative paths resolve beside the relay executable. Default: `logs`. |
| `keep` | Number of session logs to retain per agent. Positive integer; default: `20`. |
| `maxSizeMB` | Size limit per session log. Positive number; default: `10`. |

Log levels:

- `off`: no logs.
- `basic`: launch and lifecycle diagnostics.
- `changes`: basic logging plus translated messages.
- `full`: all relayed traffic.

Logs are separate for each agent and session. Logs can contain sensitive data, including conversation and file contents. Environment-variable masking does not redact message contents.

### Inheritance

Projects inherit shared settings from `defaults`.

- `distribution` uses the project value when present.
- `pathMappings` combines default and project entries.
- Each agent field uses the project’s value when present, otherwise the default agent’s value.
- Environment variables merge in this order: defaults → project → selected agent settings.
- Log settings merge in the same order, field by field. Later values win.

Agent-level `env` and `log` objects are selected as whole objects before merging. For example, a project’s `agents.claude.env` replaces `defaults.agents.claude.env`; it does not extend it. Shared `defaults.env` and project `env` still apply.

Omit fields to inherit them. `null` is not supported.

## License

[MIT](LICENSE).

# TODO

## Documentation (README)

- [ ] **Known limitation: resumed Claude threads show Linux paths.** Zed's Claude adapter replays thread
      history by reading `<session-id>.jsonl` directly from `CLAUDE_CONFIG_DIR`
      (`\\wsl.localhost\<distro>\home\<user>\.claude`), not through the relay. Old tool calls in resumed
      threads therefore show Linux paths (e.g. `/mnt/c/...`). The relay cannot fix this without rewriting
      transcript files, which is out of scope (V13). Document it in the README. (DECISIONS D11)
- [ ] **Zed `agent_servers` entries.** Copy the working Claude and Codex entries from the global Zed
      `settings.json` (`%APPDATA%\Zed\settings.json`, currently pointing to the POC wrappers) into the
      README, with paths changed to `agent-relay-claude.exe` / `agent-relay-codex.exe`. (SPEC §13)

## Verification

- [ ] **Codex opaque fields.** With logging on, run a Codex session in Zed that reads and edits files, runs a
      command and asks for approval. Check the log's `field=` entries: every translated field must really
      be a path; add any content field that was translated to the opaque list in `CodexRules.cs`. Do the same
      for Claude. (SPEC §8.2–8.4, DECISIONS D2)
- [ ] **Job object teardown:** when the relay's job object kills `wsl.exe`, check that the Linux agent process
      also ends (`ps` in the distro). (SPEC §9)
- [ ] **wsl.exe command-line form** (`--exec` vs `--`): run test T-L7 on Windows, pick the variant, update
      R6, SPEC and the README. (SPEC §6.1, §12.4)

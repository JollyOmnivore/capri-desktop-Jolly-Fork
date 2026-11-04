# Experimental DSH backend for Capri on Mac

The `codex/dsh-backend` branch adds an optional chat backend using DeepSeek Harness. It is off by default. The existing Capri engine stays available. The installed Capri app is not replaced by building this branch.

This first stage supports local Ollama, including `qwen3.5:9b`, through its loopback OpenAI-compatible endpoint. It uses DSH's agent loop, tool results, context compaction and session log. Capri supplies its current personality, notes and recent conversation, stores completed replies in its usual conversation, and speaks them through its existing voice/effects pipeline. A fresh DSH process and session handle each turn; shell state does not carry between turns. Recent conversation is context, not a request to replay earlier actions.

DSH's own tools are `read`, `write`, `edit` and `bash`. The native bridge also offers Capri's supported search, browser, screen, apps, music/media, window, notes, reminders, chess, settings, status and animation tools, using the current platform-filtered schemas. Unsupported choices are removed; routines, power operations, autonomous computer jobs and external MCP servers are not bridged. Each operation requires its own Capri approval displaying the exact arguments. Deny, closing the window, cancellation or a lost connection cannot grant permission. There is no blanket approval option. Up to twelve tool calls are admitted per turn, shell calls have a thirty-second cap, and a turn has a five-minute limit. Commands cannot request unrestricted sandbox access, and background execution is disabled in the tool configuration. Cancellation shuts down DSH and its managed subprocesses, with forced process-tree termination as a fallback. Completed side effects are not rolled back or automatically retried.

Bridged calls execute in Capri through its existing dispatcher and Security gate. Screen viewing uses Capri's existing capture and vision path and returns the resulting text description to DSH; it still requires macOS permission and a vision-capable local model. No screenshot is guessed or read by the DSH shell. Existing direct chess, notes and Mainframe commands still work. A native Security hold can be confirmed using Capri's existing yes/no flow. Native operations that have already begun may take their normal timeout to settle after cancellation; their completed effects cannot be undone. Automatic check-ins and screen observations continue through the original engine. DSH does not receive cloud or work gateway credentials; selecting a cloud/provider server while DSH is enabled produces an explicit error rather than silently forwarding its key. This is an experimental route for ordinary typed and push-to-talk chat, not a complete harness migration.

## Setup

Install Node 24.9 or newer, then run from the repository:

```sh
sh ci/setup-dsh.sh
dotnet build cross/Capri.Cross.csproj -c Release -o out/dsh-cross
out/dsh-cross/Capri
```

Quit the existing Capri instance before launching another interactive copy. Keep the currently working app bundle until this branch is ready for daily use; changing a Mac bundle can require regranting screen/microphone permissions.

Open Capri Mainframe and use **DSH backend** in the BRAIN section. **DSH setup** opens the runtime and workspace configuration; the previous More options entry also remains available. The setup can detect the repository's installed runtime in build output. Enter the absolute Node executable and runtime folder printed by setup if detection is unavailable, and select a workspace. Choose your local Ollama model under Brain, then enable DSH. Your existing model choice is used; the default Ollama port is 11434. Switching the Mainframe toggle off restores the Capri chat engine and cancels the pending DSH turn. An invalid setup leaves the toggle off and opens configuration with the error.

Start in the dedicated default `dsh-workspace` with a disposable text file. Ask Capri to read it, create another file, then run a harmless command such as `pwd`. Review each approval. Test Deny before using an important project folder.

The pinned npm runtime is installed in ignored `out/dsh-runtime`. Its lockfile records integrity hashes, and setup disables lifecycle scripts. DSH is pinned to `0.2.1-rc.5`; patched overrides pin Zod 4.6.7, Undici 8.12.2, fflate 0.8.3 and MCP client 2.3.0. The dependency audit reported zero known vulnerabilities after these overrides on November 4, 2026. That is not a complete security audit of DSH.

## Permissions and local data

On Mac, DSH's Seatbelt command sandbox allows writes in the selected workspace and platform temporary directories and fails closed if confinement is unavailable. File tools additionally check canonical paths before and after approval and reject operations outside the selected workspace, including symlink escapes. File containment retains the underlying filesystem provider's documented concurrent-symlink race; it is not isolation against a hostile local process.

**The command sandbox does not hide other readable files or block network access.** An approved shell command can read outside the workspace or send data over the network. An innocent-looking executable can also have side effects. Review commands and operate on trusted projects. A workspace directory alone is not a privacy boundary. Never approve a command simply because the model says it is safe.

Settings are in `~/Library/Application Support/Capri/dsh.json`, outside the repository. `dsh.json` is also ignored if placed in a checkout. It contains Node, Runtime and Workspace paths and an optional Port for a loopback model server, not API keys. DSH does not load the project's or the user's `.env`, credentials store, default home, telemetry plugins, web server, plugin market, agent instructions or third-party MCP servers. The subprocess receives only a small system environment allowlist and a dummy local-model credential. Each run uses a fresh private directory under `~/Library/Application Support/Capri/dsh/runs`, so shared DSH profile patches cannot silently add capabilities. Session logs and the generated persona configuration remain there as local plaintext and may contain conversation, file contents and command output. They are not committed; remove old run directories with Capri closed if you do not want that additional history retained.

## Validation

Install the pinned runtime first. The runtime checks use a loopback mock model and disposable files, not personal files or real API keys:

```sh
dotnet run --project tests/DshChecks/DshChecks.csproj -c Release -- out/dsh-runtime
dotnet run --project tests/ChessChecks/ChessChecks.csproj -c Release -- "$PWD/out/dsh-ui-checks" --dsh
dotnet run --project tests/WebSearchChecks/WebSearchChecks.csproj -c Release
```

The first suite runs the actual DSH SDK, checks read/write/bash chaining, exact approvals, denied writes, sandbox escapes, symlinks, escalation, activity events, stale turns and cancellation. Add `--live` to also exercise an already installed `qwen3.5:9b` on local Ollama, approving a fixture read and a search-to-open chain with fixture results and browser launches intercepted. The graphical suite checks the setup and approval controls without running tool operations. Existing activity, microphone, sync, speech and chess suites remain available as documented in `CONTRIBUTING.md`.

Search-to-open turns are restricted to search followed by one exact returned link. Text-only searches cannot open a browser; source text cannot add file, shell or other desktop operations to those turns. If the model omits the requested browser call, Capri reports that opening was not confirmed, rather than displaying the model's success claim. Host-side validation rejects unknown tools, unsupported platform choices and malformed arguments before approval or dispatch.

Next steps are unifying durable session history and the remaining automatic interactions, then separately considering autonomous computer jobs and explicit external MCP connections. Keep provider migration separate so private gateway configuration stays local.

References: [DSH repository](https://github.com/deepseek-ai/deepseek-harness), [published SDK minimal profile](https://www.npmjs.com/package/@deepseek-ai/dsh-sdk-minimal/v/0.2.1-rc.5).

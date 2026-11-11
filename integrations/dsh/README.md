# Experimental DSH backend for Capri on Mac

The `codex/dsh-backend` branch adds an optional chat backend using DeepSeek Harness. It is off by default. The existing Capri engine stays available. The installed Capri app is not replaced by building this branch.

This first stage supports local Ollama, including `qwen3.5:9b`, through its loopback OpenAI-compatible endpoint. It uses DSH's agent loop, tool results, context compaction and session log. Capri supplies its current personality, notes and recent conversation, stores completed replies in its usual conversation, and speaks them through its existing voice/effects pipeline. A fresh DSH process and session handle each turn; shell state does not carry between turns. Recent conversation is context, not a request to replay earlier actions.

DSH's own tools are `read`, `write`, `edit` and `bash`. The native bridge also offers Capri's supported search, browser, screen, apps, music/media, window, notes, reminders, chess, settings, status and animation tools, using the current platform-filtered schemas. Unsupported choices are removed; routines, power operations, autonomous computer jobs and external MCP servers are not bridged. Each operation requires its own Capri approval displaying the exact arguments. Deny, closing the window, cancellation or a lost connection cannot grant permission. There is no blanket approval option. Computer-enabled model steps have a 16,384-token output budget; ordinary DSH steps retain 4,096. Up to twelve tool calls are admitted per turn (twenty-four with computer use enabled), shell calls have a thirty-second cap, and a turn has a five-minute limit. Commands cannot request unrestricted sandbox access, and background execution is disabled in the tool configuration. Cancellation shuts down DSH and its managed subprocesses, with forced process-tree termination as a fallback. Completed side effects are not rolled back or automatically retried.

Bridged calls execute in Capri through its existing dispatcher and Security gate. Screen viewing uses Capri's existing capture and vision path and returns the resulting text description to DSH; it still requires macOS permission and a vision-capable local model. No screenshot is guessed or read by the DSH shell. Existing direct chess, notes and Mainframe commands still work. A native Security hold can be confirmed using Capri's existing yes/no flow. Native operations that have already begun may take their normal timeout to settle after cancellation; their completed effects cannot be undone. Automatic check-ins and screen observations continue through the original engine. DSH does not receive cloud or work gateway credentials; selecting a cloud/provider server while DSH is enabled produces an explicit error rather than silently forwarding its key. This is an experimental route for ordinary typed and push-to-talk chat, not a complete harness migration.

## Setup

Install Node 24.9 or newer, then run from the repository:

```sh
sh ci/setup-dsh.sh
dotnet build cross/Capri.Cross.csproj -c Release -o out/dsh-cross
out/dsh-cross/Capri
```

For a Finder-ready Mac app with .NET included, run `sh ci/package-dsh-mac.sh` (or set `CAPRI_DOTNET` to the repository SDK executable). This updates the normal `out/Capri.app` with Capri's standard bundle identifier, including the optional DSH backend and current fixes. Quit Capri before updating it. Use this same app path for subsequent builds; separate preview apps are no longer the default. `CAPRI_APP_BUNDLE` can override the destination. Open the app from Finder; it does not need a system .NET installation. To open Mainframe immediately, use `open "out/Capri.app" --args --mainframe`. The raw `out/dsh-cross/Capri` development executable above requires .NET to be installed or `DOTNET_ROOT` to point to the repository SDK.

Quit the existing Capri instance before launching another interactive copy. Native macOS Quit requests are routed through Capri's shutdown path so Mainframe cannot cancel exit after the character window has already closed. This prevents Bring here from trying to reopen a permanently closed window. Keep the currently working app bundle until this branch is ready for daily use; changing a Mac bundle can require regranting screen/microphone permissions.

Open Capri Mainframe and use **DSH backend** in the BRAIN section. **DSH setup** opens the runtime and workspace configuration; the previous More options entry also remains available. The setup can detect the repository's installed runtime in build output. Enter the absolute Node executable and runtime folder printed by setup if detection is unavailable, and select a workspace. Choose your local Ollama model under Brain, then enable DSH. Your existing model choice is used; the default Ollama port is 11434. Switching the Mainframe toggle off restores the Capri chat engine and cancels the pending DSH turn. An invalid setup leaves the toggle off and opens configuration with the error.

Start in the dedicated default `dsh-workspace` with a disposable text file. Ask Capri to read it, create another file, then run a harmless command such as `pwd`. Review each approval. Test Deny before using an important project folder.

The pinned npm runtime is installed in ignored `out/dsh-runtime`. Its lockfile records integrity hashes, and setup disables lifecycle scripts. DSH is pinned to `0.2.1-rc.5`; patched overrides pin Zod 4.6.7, Undici 8.12.2, fflate 0.8.3 and MCP client 2.3.0. The dependency audit reported zero known vulnerabilities after these overrides and the desktop driver addition on November 5, 2026. That is not a complete security audit of DSH.

## Computer use with visible controls

### Task queue

**Mainframe > DSH setup > Break desktop requests into a visible task queue** is enabled by default when computer use is enabled. It applies to explicit computer requests and action requests such as opening an app and navigating to a view. Explanatory questions and the existing search/search-to-open routes keep their normal workflow. Turning the queue off restores the single-session computer loop.

The local model first produces a bounded plan without tools or desktop actions. Up to six steps appear in the existing computer controls, with waiting, running, done and blocked states. Each step starts a fresh DSH session with the original request, only brief verified summaries of completed steps, and the current step's success check. Earlier screenshots, tool transcripts and element references are not copied into the new session. This reduces accumulated input context; it does not guarantee that an individual model call cannot reach its limit.

Each step has one enforced action type: observe, open an app, click, type, key or scroll. Only the matching computer input tool is exposed, and at most one input is admitted. Fresh observation is required before computer input and after it before completion. The model must submit a bounded result and evidence; this is model interpretation of fresh state, not a guarantee that its interpretation is correct. All existing operation approvals and Security checks remain. Denial, tool errors, unverified completion and output limits block the queue without replaying actions. Pause holds the transition acknowledgment before the next step; Stop cancels the complete request. Completed steps are reported in the final reply and `queue.state` activity records. This first version does not persist or automatically resume queues after restarting the app. Steps allow up to eight tool calls, the entire queue up to 48, and the existing five-minute request timeout still applies.

Queue checks use synthetic windows and local mock servers:

```sh
node tests/ComputerChecks/queue.mjs
node tests/ComputerChecks/runtime.mjs --queue
node tests/ComputerChecks/runtime.mjs --queue --queue-denied
node tests/ComputerChecks/runtime.mjs --queue --queue-extra-input
node tests/ComputerChecks/runtime.mjs --queue --local-model --calculator
```

In **Mainframe > DSH setup**, select **Enable computer use · Mac mouse and keyboard**, then save. This is independently off by default. Open **Mac permissions...** for current Accessibility, Screen Recording and microphone status, individual requests, and **Request required permissions**. That flow requests these three grants without recording audio or capturing the screen. Automation and Files & Folders have separate System Settings buttons; Automation grants are per target app, so macOS has no single request-all prompt. Allow the preview app in System Settings and restart it after granting access. Setup displays current permission status; missing grants produce an explicit tool error without sending input. Capri no longer runs automatic `tccutil reset` commands. Rebuilding an ad-hoc signed preview may still require regranting access. Set `CAPRI_CODESIGN_IDENTITY` to an existing stable code-signing identity when packaging to preserve a consistent signer. Alternatively, save the identity fingerprint (or exact certificate name) in `out/codesign-identity`; that local file is ignored by Git and reused automatically. An explicitly configured or saved identity must resolve to exactly one valid identity before publishing begins. If it is missing, expired or ambiguous, packaging stops without an ad-hoc fallback. With no identity configured, the initial default remains ad-hoc. The script never creates certificates or changes Keychain trust.

The pinned Cua Driver SDK (`@trycua/cua-driver` 0.28.0) runs in the DSH child process. Capri registers seven bounded tools through DSH's MCP result adapter: app discovery, window discovery, window state, click, type, key and scroll. It does not expose the driver's clipboard, browser-profile, permission-changing, recording, arbitrary file or configuration tools. Existing `open_app` uses Capri's verified Mac launcher. Computer tool descriptions are kept concise and omit unsupported foreground fallback instructions. Computer mode hides the older `list_windows`, `shortcut` and `app_window` routes so the model uses the computer provider instead of mixing in legacy Mac Automation controls. This is a Capri provider around the native SDK, not a mount of its entire catalog.

Explicit “computer tools” / “computer use” requests start from the current request, personality and notes, without replaying earlier chat as desktop evidence. The model receives fresh driver grant flags. Qwen sessions with computer use enabled disable template thinking (`chat_template_kwargs.enable_thinking=false`) so deliberation cannot consume the output budget before a tool call. This includes naturally worded tasks on the general DSH route. Sessions with computer use off and other models retain their existing behavior. Discovery includes only visible windows on the current desktop. File/shell tools are withheld for those turns. A completed reply with no tool calls gets one corrective observation attempt; if no computer tool runs, Capri reports that clearly. This recovery never follows a tool attempt or denial, so it cannot replay completed input. Explicit read-only requests expose observation tools only.

A **Computer use** activity window shows the current phase, **Pause/Resume**, and **Stop**. Before input, the selected element/window is outlined and Capri's artist cursor appears over its target. The cursor stays at the last attempted point until the turn ends. Both overlays are natively click-through and hide during observations; the cursor returns after capture. It marks an attempted target, not proof of a successful click. Fresh window results also give the model the last attempted target and the current snapshot's element references. An index without an explicit snapshot ID binds only to the latest fresh snapshot; explicitly stale IDs are still refused. Background delivery targets one exact visible window on the current desktop; this preview does not move the user's physical mouse or automatically retry with foreground input. Some apps reject background input and require a manual step. Pause blocks admission of the next operation; input already sent cannot be rolled back. Stop cancels the DSH turn and closes its controls.

Every operation still needs its own approval. Input requires a recent snapshot of the same window. After each input a new observation is required before another input or successful completion. Stale elements, missing windows, out-of-window targets, secure accessibility fields, Capri's own windows and unsupported coordinate modes fail closed. Only one computer tool runs at a time. These checks reduce mistaken input; they do not make an approved desktop action harmless. Inspect the target and the exact text/keys before allowing it.

Ollama's `/api/show` must positively declare `vision` before screenshots are captured and admitted to the model. Other models use accessibility elements and cannot click guessed screenshot coordinates. Screenshots and trees remain local to the configured loopback model. Computer-enabled turns remove their temporary DSH run directory on normal exit/cancellation; a crash can leave local remnants. The folder is outside the repository.

For a first test, open a disposable document yourself and ask: **“Use computer tools to inspect the visible test document, type hello in its text field, then inspect it again to verify. Do not save or send anything.”** Approve each step and test Pause/Stop. Avoid important documents for the first run. macOS permission grants must be completed by the user; the automated tests do not grant them or type into personal apps.

## Permissions and local data

Mac `open_app` calls use the shared native launcher in both engines. It discovers app bundles in the normal user and system application folders, including nested folders, reads bundle display names and IDs, and resolves common aliases such as VS Code, Chrome and Teams. Equally good matches ask for clarification; exact bundle IDs and catalog paths can disambiguate. The launcher checks the selected bundle's actual running process and reports new launches, already-running apps and missing visible windows separately. A missing app refreshes the catalog once; a moved bundle is retried only after the first path is confirmed missing and a unique replacement is found. Timeouts and uncertain startup never trigger an automatic duplicate launch. Steam-only entries without an app bundle still use Capri's existing Steam launch route.

On Mac, DSH's Seatbelt command sandbox allows writes in the selected workspace and platform temporary directories and fails closed if confinement is unavailable. File tools additionally check canonical paths before and after approval and reject operations outside the selected workspace, including symlink escapes. File containment retains the underlying filesystem provider's documented concurrent-symlink race; it is not isolation against a hostile local process.

**The command sandbox does not hide other readable files or block network access.** An approved shell command can read outside the workspace or send data over the network. An innocent-looking executable can also have side effects. Review commands and operate on trusted projects. A workspace directory alone is not a privacy boundary. Never approve a command simply because the model says it is safe.

Settings are in `~/Library/Application Support/Capri/dsh.json`, outside the repository. `dsh.json` is also ignored if placed in a checkout. It contains Node, Runtime and Workspace paths, an optional Computer opt-in and Port for a loopback model server, not API keys. DSH does not load the project's or the user's `.env`, credentials store, default home, telemetry plugins, web server, plugin market, agent instructions or third-party MCP servers. The subprocess receives only a small system environment allowlist and a dummy local-model credential. Each run uses a fresh private directory under `~/Library/Application Support/Capri/dsh/runs`, so shared DSH profile patches cannot silently add capabilities. Session logs and the generated persona configuration remain there as local plaintext and may contain conversation, file contents and command output. They are not committed; remove old run directories with Capri closed if you do not want that additional history retained.

## Local activity and permission diagnostics

In **DSH setup**, enable **Record local DSH activity**, save settings, and reproduce one problem. **View DSH activity** opens a read-only viewer with a refresh button. Logs are stored in `~/Library/Application Support/Capri/dsh/activity.jsonl`; a previous file is retained after rotation at 2 MiB. Files are private to the current user. Logging is off by default and does not reconstruct earlier runs.

The log records the current user request, visible assistant replies, tool calls/results, approval decisions, model stop reasons, and permission checks. Images and reasoning are omitted, common credential patterns are redacted, and large records are bounded. File/screen text can still be present: review before sharing. Nothing is uploaded, and logs are outside the checkout. The temporary computer-run directory is still removed normally; the activity log persists separately.

**Mac permissions > Check DSH driver permissions** runs a prompt-free check inside a fresh DSH-style child process and saves its report to the activity log. It compares Capri's own checks with driver Accessibility/Screen Recording flags and process attribution. It does not capture the screen or verify live ScreenCaptureKit readiness; a granted flag alone cannot prove that a subsequent capture will succeed. Tool results in a reproduced turn supply the actual capture refusal/error. The model should report that result rather than guessing which permission is missing.

## Validation

Computer-specific checks use synthetic desktop data and disposable graphical windows:

```sh
node tests/ComputerChecks/policy.mjs
node tests/ComputerChecks/runtime.mjs --fixture
node tests/ComputerChecks/runtime.mjs
node tests/ComputerChecks/runtime.mjs --local-model
node tests/ComputerChecks/runtime.mjs --local-model --calculator
node tests/ComputerChecks/runtime.mjs --local-model --read-only
node tests/ComputerChecks/runtime.mjs --no-tools
node tests/ComputerChecks/runtime.mjs --never-tools
node tests/ComputerChecks/runtime.mjs --computer-denied
dotnet run --project tests/ChessChecks/ChessChecks.csproj -c Release -- "$PWD/out/computer-ui-checks" --computer
```

The fixture runtime test uses the real DSH agent loop and image adapter with a synthetic driver. The optional `--local-model` check uses installed local Qwen, real driver tool schemas and a synthetic text field to exercise discovery, typing and verification without interacting with personal apps. It tests accessibility-only operation; the separate fixture check covers image admission. The other runtime test loads the actual native driver catalog but denies observation before any desktop content is read. UI checks verify Pause/Stop, Retina positioning, native click-through and overlay cleanup. Live mouse/keyboard operation in another app requires user-granted Accessibility and is a separate manual check. Failed model turns now preserve their actual terminal error or output-token limit, and failed tool messages remain visible instead of being replaced by a generic empty-reply error. Failures are redacted and recorded in Capri's existing local `errors.txt`, outside the repository; successful screen contents and screenshots are not added to that error log.


Install the pinned runtime first. The runtime checks use a loopback mock model and disposable files, not personal files or real API keys:

```sh
dotnet run --project tests/DshChecks/DshChecks.csproj -c Release -- out/dsh-runtime
dotnet run --project tests/ChessChecks/ChessChecks.csproj -c Release -- "$PWD/out/dsh-ui-checks" --dsh
dotnet run --project tests/WebSearchChecks/WebSearchChecks.csproj -c Release
```

The first suite runs the actual DSH SDK, checks read/write/bash chaining, exact approvals, denied writes, sandbox escapes, symlinks, escalation, activity events, stale turns and cancellation. Add `--live` to also exercise an already installed `qwen3.5:9b` on local Ollama, approving a fixture read and a search-to-open chain with fixture results and browser launches intercepted. The graphical suite checks the setup and approval controls without running tool operations. Add `--bring` instead of `--dsh` to stress Bring here during replies, typing, hiding and stacking and verify native Quit completes without leaving closed windows behind. Existing activity, microphone, sync, speech and chess suites remain available as documented in `CONTRIBUTING.md`.

Search-to-open turns are restricted to search followed by one exact returned link. Text-only searches cannot open a browser; source text cannot add file, shell or other desktop operations to those turns. If the model omits the requested browser call, Capri reports that opening was not confirmed, rather than displaying the model's success claim. Host-side validation rejects unknown tools, unsupported platform choices and malformed arguments before approval or dispatch.

Next steps are unifying durable session history and the remaining automatic interactions, then separately considering autonomous computer jobs and explicit external MCP connections. Keep provider migration separate so private gateway configuration stays local.

References: [DSH repository](https://github.com/deepseek-ai/deepseek-harness), [published SDK minimal profile](https://www.npmjs.com/package/@deepseek-ai/dsh-sdk-minimal/v/0.2.1-rc.5).

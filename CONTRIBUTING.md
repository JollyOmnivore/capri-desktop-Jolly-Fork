# Contributing to Capri

Thanks for helping her out, choom.

## Before you start

- **Small fixes:** open a pull request.
- **New features or big changes:** ask first in the [Discord](https://discord.gg/F4fDHfGtFk) or open an issue, so nobody builds something that won't be merged.
- **Licence:** Capri is under the [PolyForm Noncommercial License](LICENSE.md). By opening a pull request you agree your changes are shared under it too.

## Where things live

| Folder | What's in it |
|---|---|
| `app/` | The Windows app (C# 5, .NET Framework 4.8, WinForms) |
| `cross/` | The Mac and Linux app (.NET 10, Avalonia). Shares `Brain.cs`, `Engine.cs`, `Hands.cs` and `Voice.cs` with `app/` |
| `assets/sprites/` | Her animation strips |
| `site/` | The website |
| `ci/` | Build scripts the CI uses |

## Build and test

**Windows** (needs Windows 10 or 11 and the Windows 10/11 SDK):

```powershell
./ci/build-windows.ps1 -SmokeTest
```

This builds `out/windows/Capri.exe` and `Capri-Setup.exe`, then runs her smoke test. Every line should say `PASS`.

**Mac and Linux** (needs the [.NET 10 SDK](https://dotnet.microsoft.com/download)):

```bash
dotnet build cross/Capri.Cross.csproj -c Release -o out/cross
```

Then run her smoke test, and check every line says `PASS`:

```bash
out/cross/Capri --smoke out/cross-smoke
```

On Windows, use `out\cross\Capri.exe` instead. CI runs all three on every pull request.

The work gateway checks use fake keys and a local mock server, without opening Capri or contacting an AI provider:

```bash
dotnet run --project tests/GatewayChecks/GatewayChecks.csproj -c Release
```

Mac builds also compile `cross/mac/Microphone.swift` with Xcode Command Line Tools. Local push-to-talk uses Whisper base through Whisper.net; audio is recorded only while TALK is held, with a 30-second cap. The model is downloaded once with a pinned SHA-256. The native helper lists Core Audio inputs without recording; push-to-talk routes an Audio Queue to the selected device and writes 16 kHz mono PCM. Microphone access needs a bundled app with `NSMicrophoneUsageDescription` from `cross/mac/Info.plist`.

To test transcription and recording cleanup without opening the microphone, supply the pinned model and the public `samples/jfk.wav` from whisper.cpp:

```bash
dotnet run --project tests/LocalSpeechChecks/LocalSpeechChecks.csproj -c Release -- /path/to/ggml-base.bin /path/to/jfk.wav
```

Pocket TTS checks exercise process cancellation, temporary file cleanup, WAV validation and the shared audio format without a model download:

```bash
dotnet run --project tests/PocketVoiceChecks/PocketVoiceChecks.csproj -c Release
```

Add `-- --live /path/to/test-data` to install the pinned official runtime and generate real speech without playing it. Its public model cache lives under `test-data/pocket-tts/cache`. The app smoke test also checks provider switching, saved voice selection, all effects and layers, and audio completing after the text fallback.

Readable web search checks use fake search results and loopback mock servers:

```bash
dotnet run --project tests/WebSearchChecks/WebSearchChecks.csproj -c Release
```

The same suite tests search-to-open sequencing, returned-link validation, rejected extra tools, permission holds and cancellation against a mock model API. Add `-- --live http://127.0.0.1:8080 qwen3.5:9b` to also exercise an existing local SearXNG instance and a local Ollama model with public test queries, including finding and selecting the official Pocket TTS repo. Browser launches are intercepted in the tests. The normal checks never contact a search engine or use real credentials. The app smoke test uses a search fixture.

Native Mac/Linux chess uses the same rules, engine and game state as Windows. Dedicated checks exercise real routed pointer events and controls, including promotion, hints, worker cancellation and offline chat commands, and save board renders at 1x and 2x:

```bash
dotnet run --project tests/ChessChecks/ChessChecks.csproj -c Release -- /absolute/path/to/chess-checks
```

These checks need a graphical session but do not move the real mouse, record audio, call an AI model or use a paid voice. Their settings are isolated in the output folder.

The sync integration checks use the same graphical test host to exercise Mainframe voice modes, editable personality migration and upstream provider security regressions, including redirected requests, key isolation, redaction and forgotten-key races:

```bash
dotnet run --project tests/ChessChecks/ChessChecks.csproj -c Release -- /absolute/path/to/sync-checks --sync
```

Microphone selector UI, saved device migration, refresh races and disconnected inputs can be tested without recording:

```bash
dotnet run --project tests/ChessChecks/ChessChecks.csproj -c Release -- /absolute/path/to/microphone-checks --microphone
```

The local speech checks also verify selected-device arguments, language names, silent-input diagnostics and the native PCM writer without capturing live audio.

Provider requests use loopback fixtures and fake keys. The search suite also tests the upstream planner against injected source links and text-only search requests.

Tool activity UI checks cover real dispatch callbacks, animated 1x/2x renders, text and voice completion, permission holds, failures, canceled requests, stale results and empty action promises:

```bash
dotnet run --project tests/ChessChecks/ChessChecks.csproj -c Release -- /absolute/path/to/activity-checks --activity
```

The mock web-search suite verifies activity callbacks on actual search and browser tool dispatch, including the upstream planner, and an action promise with no dispatched tool.

## House rules

- **Match the code around you.** No code comments, plain names, and the same layout as the file you're in.
- **`app/` is C# 5.** It's built with the `csc.exe` that ships with Windows, so no `$"..."` strings, `?.`, `nameof` or `=>` members.
- **Add a smoke check** for anything new she does. Look for `checks.Add("PASS ...")` in `app/Capri.cs` or `cross/Smoke.cs`.
- **Her look is strict.** Changes to her art or face need Lachy's OK first.
- **No new network calls or tracking** without asking. API keys stay encrypted on the user's PC.
- **Ask before risky actions.** Anything that sends, posts, buys or deletes must go through her ask step, unless the user asked for that exact thing.
- **Commit messages** say what she does now, in plain words. For example: `She dances when music plays`.

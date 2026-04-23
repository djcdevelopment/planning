# Phase 7 — Stream D status

## [RESEARCH-DONE]

Scope verified. Findings:

- `ISshService` already exists as the SSH-executor seam — `src/Farmer.Core/Contracts/ISshService.cs` (interface) + `src/Farmer.Tools/SshService.cs` (Renci.SshNet impl with connection reuse per VM). `DeliverStage` and `DispatchStage` already consume it; `DeliverStageTests` already has a `MockSshService` fake pattern. No new seam needed — `SshWorkerFileReader` depends on `ISshService` and tests reuse the same fake pattern.
- `RunDirectoryLayout` already owns canonical VM-side paths (`VmManifest`, `VmSummary`, `VmProjectRoot`, etc.) — cleaner to use those for the well-known manifest/summary/timing paths, and only hand-build paths for the generic `relativePath` variants the interface still exposes. But since the interface contract passes `relativePath` from the caller, reader stays generic — just joins `vm.RemoteProjectPath` + normalized relative path.
- Baseline test count is 143 green (per Stream A status), not 133. Stream A and B both landed on `main` already; this worktree already has those.
- `FarmerSettings.SshCommandTimeoutSeconds` (default 30) is used by `SshService.ExecuteAsync` as the default when `timeout` is null — so we can just let `ISshService` apply it. `ProgressPollIntervalMs` (default 2000) exists for the wait loop.
- Interface contract subtleties from existing `MappedDriveReader`: `ListFilesAsync` returns **filenames only** (via `Path.GetFileName`), sorted, empty list when directory missing. Must preserve that semantics — `ls -1 -- dir/pattern` returns full paths-as-globbed relative to dir, but `ls -1 dir/` returns basenames. Use `cd dir && ls -1 pattern` or similar to get just names. Actually `ls -1 dir/*.pattern` returns paths like `dir/foo.txt` — need `basename`. Simplest: `cd <dir> 2>/dev/null && ls -1 <pattern> 2>/dev/null` so output is already basenames.

Surprises:

- Interface arg name is `relativePath` but `EmitPromptSpansAsync` in CollectStage already passes `"output/per-prompt-timing.jsonl"` with forward slashes (and `.Replace('/', Path.DirectorySeparatorChar)` for Windows). Need to normalize back to `/` for Linux target regardless of what the caller passes.
- `MappedDriveReader.FileExistsAsync(vmName, ...)` only takes a relative file path, not a dir — but `CollectStage` doesn't use it for dirs. We'll implement `test -f`; `ListFilesAsync` uses `test -d`-equivalent (trying to `cd` and exit != 0).

## [DESIGN-READY]

Concrete diff shape:

**New — `src/Farmer.Tools/SshWorkerFileReader.cs`** (~110 lines)
- `public sealed class SshWorkerFileReader : IMappedDriveReader`
- ctor: `(ISshService ssh, IOptions<FarmerSettings> settings, ILogger<SshWorkerFileReader> logger)`
- Private helpers: `ResolveVm(vmName)`, `ResolveRemotePath(vm, relativePath)` (joins `vm.RemoteProjectPath` + relative, normalizes `\`→`/`), `ShellQuote(path)` (wraps in single quotes, escapes inner `'` as `'\''`).
- `ReadFileAsync`: `ssh.ExecuteAsync(vmName, $"cat -- {quoted}")`. If exit != 0, throw `FileNotFoundException` with stderr.
- `FileExistsAsync`: `ssh.ExecuteAsync(vmName, $"test -f {quoted} && echo 1 || echo 0")`. Parse stdout trim == "1".
- `WaitForFileAsync`: loop `FileExistsAsync` + `Task.Delay(ProgressPollIntervalMs)` until deadline; on hit, `ReadFileAsync`. No SSHFS cache delay. Throw `TimeoutException` (same message shape as MappedDriveReader).
- `ListFilesAsync`: `ssh.ExecuteAsync(vmName, $"cd {quotedDir} 2>/dev/null && ls -1 -- {quotedPattern} 2>/dev/null")`. Exit != 0 → empty list. Split on `\n`, trim, drop empties, sort, return.

**New — `src/Farmer.Tests/Tools/SshWorkerFileReaderTests.cs`** (~180 lines)
- Private `MockSshService : ISshService` inside test class (same pattern as `DeliverStageTests`), plus a queue-based result dispenser so tests can seed `cat`/`test -f`/`ls -1` sequences.
- Tests:
  - `ReadFileAsync_ReturnsStdout_OnExitZero`
  - `ReadFileAsync_Throws_OnNonZeroExit`
  - `ReadFileAsync_UsesCat_WithQuotedAbsolutePath` (asserts the command shape)
  - `FileExistsAsync_Returns_True_On_1Output`
  - `FileExistsAsync_Returns_False_On_0Output`
  - `WaitForFileAsync_ReturnsContents_AfterPollHit`
  - `WaitForFileAsync_Throws_OnTimeout`
  - `ListFilesAsync_SplitsOnNewlines_AndSorts`
  - `ListFilesAsync_ReturnsEmpty_OnNonZeroExit`
  - `ResolvePath_NormalizesBackslashes_ToForwardSlashes`
  - `Quoting_EscapesSingleQuotes_InPath` (defensive)

**Modified — `src/Farmer.Host/Program.cs`** (1 line)
- `AddSingleton<IMappedDriveReader, MappedDriveReader>()` → `AddSingleton<IMappedDriveReader, SshWorkerFileReader>()`. `ISshService` is already registered above, so no additional DI needed.

**Modified — `src/Farmer.Tests/Workflow/DiCompositionTests.cs`** (1 line)
- Swap the registration line inside `BuildProvider` to match. Tests only assert interface resolution (`GetRequiredService<IMappedDriveReader>()`), so no assertion changes needed.

No new interface. No touch to `MappedDriveReader.cs`, `IMappedDriveReader.cs`, `VmConfig.cs`, `FarmerSettings.cs`, or any caller stage.

## [COMPLETE] 9ef8ea3

Verification gates:

- `dotnet build src/Farmer.sln`: clean, 0 warnings, 0 errors.
- `dotnet test src/Farmer.sln`: **157 green** (152 unit + 5 integration, 0 failed, 0 skipped). Baseline 143 preserved; 14 new unit tests additive in `SshWorkerFileReaderTests`.
- Sanity-grep: `SshWorkerFileReader` referenced by `Program.cs`, `DiCompositionTests.cs`, and the two new files only. `MappedDriveReader` the class stays in its own file untouched; the interface `IMappedDriveReader` is still consumed by `CollectStage`, `RetrospectiveStage`, and their test mocks (all untouched per spec).

Files touched (4):

- `src/Farmer.Tools/SshWorkerFileReader.cs` (new, ~140 lines)
- `src/Farmer.Tests/Tools/SshWorkerFileReaderTests.cs` (new, ~220 lines, 14 tests)
- `src/Farmer.Host/Program.cs` (1-line DI swap + comment)
- `src/Farmer.Tests/Workflow/DiCompositionTests.cs` (1-line DI swap to mirror production)

Surprises:

- The SSH executor seam **already existed** as `ISshService` — so instead of introducing a minimal `ISshCommandExecutor` interface as the spec allowed, the new reader depends on the seam `DeliverStage` and `DispatchStage` already use. Tests reuse the `MockSshService` pattern from `DeliverStageTests`. Net: one less interface in the codebase.
- `ShellQuote` is `public static` on `SshWorkerFileReader` because the test project has no `InternalsVisibleTo` from `Farmer.Tools`. Adding the attribute would have been one more file to touch for no gain; `public` on a pure function is fine. Called out in the file with a comment.
- Baseline was 143 in the Stream A status. This worktree's tests went 143 → 157 after my additions. All green.

Commit not pushed. Orchestrator owns the merge + smoke-trace re-fire.


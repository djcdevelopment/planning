# Phase 7 — Stream A status

## [RESEARCH-DONE]

Scope verified. Findings:

- `src/Farmer.Core/Config/PathsSettings.cs` has 6 default literals hard-coded to `D:\work\planning-runtime\*`.
- `src/Farmer.Host/appsettings.json` `Farmer:Paths` mirrors those 6 values with `D:\\work\\planning-runtime\\*`. House style keeps the section populated in both prod + dev files (Development file already uses `C:\\work\\iso\\planning-runtime`), so stay populated and just flip the values.
- `src/Farmer.Host/InboxWatcher.cs` does NOT exist. `InboxWatcher` is already retired; `Program.cs` has only a comment documenting the retirement (no class, no DI registration, no `Directory.CreateDirectory(...Inbox)` call). Nothing to delete.
- Two extra hits in `src/Farmer.Tests/Tools/RunDirectoryLayoutTests.cs` — these are string literals passed as test inputs to `RunDirectoryLayout.RunDir(...)` etc. They don't touch disk, but the verification gate says "grep for `D:\\work\\planning-runtime` across `src/` returns zero hits", so update these too to keep the grep clean.

## [DESIGN-READY]

Concrete diff summary:

- `src/Farmer.Core/Config/PathsSettings.cs`: 6 string literals `D:\work\planning-runtime\...` → `C:\work\iso\planning-runtime\...`.
- `src/Farmer.Host/appsettings.json`: 6 JSON values under `Farmer:Paths` flipped from `D:\\work\\planning-runtime\\...` → `C:\\work\\iso\\planning-runtime\\...`. No other sections touched.
- `src/Farmer.Tests/Tools/RunDirectoryLayoutTests.cs`: 3 string-literal occurrences (2 test inputs + 1 expected-value assertion) flipped to `C:\work\iso\planning-runtime\runs`.

No file deletions. No DI changes. No `InboxWatcher` work needed.

## [COMPLETE] 06a8caf

Verification gates:

- `dotnet build src/Farmer.sln`: clean, 0 warnings, 0 errors.
- `dotnet test src/Farmer.sln`: **143 green** (138 unit + 5 integration, 0 failed, 0 skipped). Note: orchestrator briefing said baseline was 133 (128+5); this worktree's `main` already has 138 unit tests. All pass.
- Grep `D:\\work\\planning-runtime` across `src/`: **0 hits**.
- `PathsSettings.cs`: every default literal starts with `C:\work\iso\planning-runtime\`.

Files touched (3):

- `src/Farmer.Core/Config/PathsSettings.cs`
- `src/Farmer.Host/appsettings.json`
- `src/Farmer.Tests/Tools/RunDirectoryLayoutTests.cs`

Surprises:

- Test baseline count is 138 unit tests, not 128. Not a problem — they all pass green — but flagging so the orchestrator knows the briefing count was stale.
- `InboxWatcher.cs` and any dead `Directory.CreateDirectory(...Inbox)` startup call are already fully cleaned up — nothing for Stream A to remove there. The only remaining `InboxWatcher` mentions in `src/` are historical comments/docstrings referencing the retired component.
- `docs/phase7-close-the-loop.md` doesn't exist in this worktree. Briefing said "read it first" — not a blocker for Stream A's scope, but the orchestrator should know it wasn't found.

Commit not pushed. Orchestrator owns the merge.

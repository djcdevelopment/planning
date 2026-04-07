# Project: Farmer - Claude CLI Worker Orchestration

## Lessons Learned

- **CRITICAL: When pushing code to a new branch or changing anything that requires the user to act on their Windows machine, ALWAYS put exact copy-paste commands at the TOP of the response** — branch checkout, build, run — before any explanation. Never assume the user is on the right branch or in the right directory. Never split commands across multiple messages.

## Build Commands (from repo root on Windows)

```powershell
git checkout claude/<branch-name>
dotnet build src/Farmer.sln
dotnet test src/Farmer.sln
dotnet run --project src/Farmer.Host
```

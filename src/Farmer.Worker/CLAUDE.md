# Farmer Worker — Claude CLI Instructions

You are a build worker managed by the Farmer orchestration system. The worker script (`worker.sh`) invokes you once per prompt file. You see ONE prompt at a time.

## Your Environment

- You are running on a Hyper-V Ubuntu VM
- Your project root is `~/projects/`
- You have full autonomy: any tool, any package, any command
- You are running in `--dangerously-skip-permissions` mode — all tool calls are auto-approved

## What You Should Do

1. **Read the prompt you've been given.** It describes a task (build a feature, fix a bug, write tests, etc.).
2. **Do the work.** Use whatever tools and approach you think is best. You can:
   - Read, write, and edit files
   - Run bash commands
   - Install packages
   - Download dependencies
   - Build and run code
   - Create directories
   - Do anything the VM allows
3. **Focus on quality.** If something isn't right, fix it before you finish.
4. **Say what you did.** Your final message will be captured as part of the run's execution log. Include a brief summary of what you changed and any issues you noticed.

## What You Should NOT Do

- **Do NOT touch `~/projects/.comms/`** — the worker script owns that directory for progress reporting
- **Do NOT touch `~/projects/output/`** — the worker script writes manifest.json, summary.json, and other artifacts there
- **Do NOT run `git commit` or `git push`** — the worker script handles git operations after you exit
- **Do NOT try to read other prompt files from `~/projects/plans/`** — you get one prompt at a time. Prior prompts' work is already on disk if you need context from it.

## Context You Have

- Prior prompts' file changes are already on disk — you can read them
- `~/projects/plans/task-packet.json` has metadata about the run (work request name, etc.)
- If this is a retry run, the first prompt will include reviewer feedback at the top

## Quality Bar

- Does the code compile / run without errors?
- Did you address everything the prompt asked for?
- Are there obvious bugs or missing pieces?

If you notice something wrong with your own work, fix it before finishing. Your output will be reviewed by an automated QA agent that will flag issues for future runs.

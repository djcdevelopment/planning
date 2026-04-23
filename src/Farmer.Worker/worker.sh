#!/bin/bash
#
# Farmer Worker — Phase 6 real worker
# ====================================
#
# Invoked by DispatchStage via:
#   cd ~/projects && WORK_DIR=/home/claude/runs/run-<id> bash worker.sh <run_id>
#
# Runs Claude CLI per prompt file in $WORK_DIR/plans/, captures output,
# writes manifest.json + summary.json + worker-retro.md under $WORK_DIR/output/
# for CollectStage.
#
# Full dangerous mode: --dangerously-skip-permissions, no tool allowlist,
# high max-turns. The VM is the sandbox. See ADR-008.
#
# FARMER_WORKER_MODE=fake runs without Claude CLI (canned output).
# FARMER_WORKER_MODE=fake-bad produces adversarial output on first attempt
#   (BUILD FAILED, npm errors, exit=1) so the retrospective realistically
#   verdicts Retry; on retry (task-packet.feedback populated) it falls
#   through to fake's clean output to demonstrate the feedback loop.
# Default is real.
#
# Phase 7.5 Stream F: workspace is per-run. If WORK_DIR is set (by
# DispatchStage) we honor it; otherwise we fall back to the legacy
# shared ~/projects layout so pre-F host deployments still function.
#

set -uo pipefail

# --- PATH setup (Claude installed via npm local prefix) ---
export PATH="$HOME/.npm-global/bin:$PATH"

RUN_ID="${1:-unknown}"
# WORK_DIR is the per-run workspace (Phase 7.5 Stream F). Absent => legacy
# shared-workspace behavior. Parameter expansion :- supplies the default
# without requiring the variable to be declared, matching `set -u` cleanliness.
PROJECT_ROOT="${WORK_DIR:-${HOME}/projects}"
PLANS_DIR="${PROJECT_ROOT}/plans"
OUTPUT_DIR="${PROJECT_ROOT}/output"

# Claude CLI picks up its working directory from the shell that launched it.
# DispatchStage invokes us from `cd ~/projects` (legacy convention); without
# this cd, Claude builds in the shared /home/claude/projects/ root and the
# per-run workspace at $PROJECT_ROOT stays empty — manifest comes back
# WORKER_NO_CHANGES even though Claude succeeded. See Phase-Demo rehearsal
# run-20260423-103031-7f0162 for the failure signature.
cd "$PROJECT_ROOT" || { echo "FATAL: cannot cd to $PROJECT_ROOT" >&2; exit 1; }
COMMS_DIR="${PROJECT_ROOT}/.comms"
PROGRESS_FILE="${COMMS_DIR}/worker-progress.md"
TASK_PACKET_FILE="${PLANS_DIR}/task-packet.json"

WORK_REQUEST_NAME=""
FEEDBACK=""
START_EPOCH=$(date -u +%s)
PROMPT_TOTAL=0
PROMPT_INDEX=0
WORKER_CLEAN_EXIT=""
SUMMARY_ISSUES=()
PROMPT_RESULTS=()

mkdir -p "$OUTPUT_DIR" "$COMMS_DIR"

# Phase 7.5 Stream F: per-run workspaces aren't pre-initialized as git repos
# (DeliverStage only mkdir's them). `write_manifest` runs `git status` to
# compute files_changed; without a repo it silently returns nothing and we
# fall back to the WORKER_NO_CHANGES sentinel. `git init` here is cheap and
# lets Claude's file writes flow into the manifest the same way the legacy
# shared ~/projects repo did. Guarded so we don't re-init an existing repo.
if [ ! -d "$PROJECT_ROOT/.git" ]; then
  git init --quiet "$PROJECT_ROOT" 2>/dev/null || true
fi

# --- Determine mode ---
TASK_WORKER_MODE=$(jq -r '.worker_mode // empty' "$TASK_PACKET_FILE" 2>/dev/null)
WORKER_MODE="${FARMER_WORKER_MODE:-${TASK_WORKER_MODE:-real}}"

# --- Progress writer ---
write_progress() {
  local phase="$1" msg="$2"
  local elapsed=$(( $(date -u +%s) - START_EPOCH ))
  cat > "${PROGRESS_FILE}.tmp" <<PROGRESS
---
source: worker
run_id: ${RUN_ID}
phase: ${phase}
prompt_index: ${PROMPT_INDEX}
prompt_total: ${PROMPT_TOTAL}
elapsed_sec: ${elapsed}
mode: ${WORKER_MODE}
updated: $(date -u -Iseconds)
---
${msg}
PROGRESS
  mv "${PROGRESS_FILE}.tmp" "$PROGRESS_FILE"
}

# --- Manifest writer ---
write_manifest() {
  local status="$1"
  local files_json
  # Compute files_changed from git status.
  # Filter toolchain noise (virtualenvs, node_modules, caches) — those aren't
  # "what the worker built" and pulling them all in blew past jq's ARG_MAX
  # on Python/JS runs (2600+ files = 200KB+ arg). Keep the manifest focused
  # on source files the retrospective agent should review.
  cd "$PROJECT_ROOT"
  git add -A 2>/dev/null
  # Write the raw file list to a temp file + feed it via --slurpfile so we
  # never hit ARG_MAX even if a sample plan generates a huge source tree.
  local files_list_tmp="$OUTPUT_DIR/.manifest-files.tmp"
  git status --porcelain=v1 2>/dev/null \
    | awk '{print $2}' \
    | grep -vE '^(\.venv/|venv/|node_modules/|\.pytest_cache/|__pycache__/|\.gradle/|\.git/|dist/|build/|target/|bin/|obj/|\.next/|\.nuxt/|\.cache/|coverage/|\.egg-info/|\.mypy_cache/|\.ruff_cache/|\.tox/)' \
    | sort -u \
    | jq -R -s 'split("\n") | map(select(length > 0))' \
    > "$files_list_tmp" 2>/dev/null

  # Fallback to sentinel if filtered list is empty or write failed
  if [ ! -s "$files_list_tmp" ] || [ "$(cat "$files_list_tmp" 2>/dev/null)" = "[]" ]; then
    echo '["WORKER_NO_CHANGES"]' > "$files_list_tmp"
  fi

  jq -n \
    --arg run_id "$RUN_ID" \
    --slurpfile files "$files_list_tmp" \
    --arg ts "$(date -u -Iseconds)" \
    --arg status "$status" \
    '{
      run_id: $run_id,
      files_changed: $files[0],
      branch_name: "",
      commit_sha: null,
      generated_at: $ts
    }' > "$OUTPUT_DIR/manifest.json"

  rm -f "$files_list_tmp"
}

# --- Summary writer ---
write_summary() {
  local desc="$1"
  local issues_json
  issues_json=$(printf '%s\n' "${SUMMARY_ISSUES[@]}" 2>/dev/null | jq -R -s 'split("\n") | map(select(length > 0))' 2>/dev/null)
  [ -z "$issues_json" ] && issues_json='[]'

  local results_text=""
  for r in "${PROMPT_RESULTS[@]}"; do
    results_text="${results_text}\n- ${r}"
  done

  jq -n \
    --arg run_id "$RUN_ID" \
    --arg desc "$desc" \
    --argjson issues "$issues_json" \
    --arg retro "## Worker Retro\n\nPrompt results:${results_text}\n\nMode: ${WORKER_MODE}" \
    --arg ts "$(date -u -Iseconds)" \
    '{
      run_id: $run_id,
      description: $desc,
      issues: $issues,
      retro: $retro,
      worker_retro: $retro,
      generated_at: $ts
    }' > "$OUTPUT_DIR/summary.json"

  # Also write standalone worker-retro.md
  printf "# Worker Retrospective\n\nRun: %s\nMode: %s\nPrompts: %d\n\n## Prompt Results\n%b\n\n## Issues\n" \
    "$RUN_ID" "$WORKER_MODE" "$PROMPT_TOTAL" "$results_text" > "$OUTPUT_DIR/worker-retro.md"
  for issue in "${SUMMARY_ISSUES[@]}"; do
    printf -- "- %s\n" "$issue" >> "$OUTPUT_DIR/worker-retro.md"
  done
  [ ${#SUMMARY_ISSUES[@]} -eq 0 ] && echo "None." >> "$OUTPUT_DIR/worker-retro.md"
}

# --- Exit trap: guarantees partial output on any exit ---
on_exit() {
  local code=$?
  if [ -z "$WORKER_CLEAN_EXIT" ]; then
    write_progress "killed_partial" "Worker exited without clean completion (code=$code)"
    write_manifest "killed_partial"
    write_summary "PARTIAL: worker exited before completion (code=$code)"
  fi
}
trap on_exit EXIT HUP TERM INT

# --- Claude CLI invocation ---
run_claude_real() {
  local prompt_file="$1"
  local prompt_body
  prompt_body=$(cat "$prompt_file")

  # Inject feedback from prior QA into first prompt
  if [ "$PROMPT_INDEX" = "1" ] && [ -n "${FEEDBACK:-}" ]; then
    prompt_body="# Reviewer feedback from a prior run

${FEEDBACK}

---

${prompt_body}"
  fi

  local stdout_file="${OUTPUT_DIR}/prompt-${PROMPT_INDEX}-stdout.txt"
  local stderr_file="${OUTPUT_DIR}/prompt-${PROMPT_INDEX}-stderr.txt"

  claude -p "$prompt_body" \
    --dangerously-skip-permissions \
    --no-session-persistence \
    --max-turns 500 \
    --output-format text \
    > "$stdout_file" 2> "$stderr_file"

  return $?
}

run_claude_fake() {
  local prompt_file="$1"
  local tag="fake-$(basename "$prompt_file" .md)"
  mkdir -p "${PROJECT_ROOT}/.farmer-fake"
  echo "fake content for $tag — $(date -u -Iseconds)" > "${PROJECT_ROOT}/.farmer-fake/${tag}.txt"
  echo "Fake worker processed $(basename "$prompt_file") successfully." > "${OUTPUT_DIR}/prompt-${PROMPT_INDEX}-stdout.txt"
  echo "" > "${OUTPUT_DIR}/prompt-${PROMPT_INDEX}-stderr.txt"
  return 0
}

# Adversarial fake mode used to demo the retry loop with a real Retry verdict.
# Writes output that the retrospective agent will realistically flag (explicit
# BUILD FAILED markers, npm error codes, exit code 1). Paired with the dispatch
# block below: on a retry attempt (FEEDBACK populated), worker.sh falls through
# to run_claude_fake instead, pretending the reviewer's feedback was addressed.
run_claude_fake_bad() {
  local prompt_file="$1"
  mkdir -p "${PROJECT_ROOT}/.farmer-fake"
  echo "[fake-bad] synthetic failure for retrospective testing $(date -u -Iseconds)" >> "${PROJECT_ROOT}/.farmer-fake/last-run.log"
  cat > "${OUTPUT_DIR}/prompt-${PROMPT_INDEX}-stdout.txt" <<EOF
BUILD FAILED -- prompt $(basename "$prompt_file")
error: unable to install required dependencies (synthetic failure, WORKER_MODE=fake-bad)
npm ERR! code ELIFECYCLE
npm ERR! Exit status 1
No files were written. Nothing compiles. No tests added.
EOF
  echo "npm ERR! (synthetic) prompt $(basename "$prompt_file") failed" > "${OUTPUT_DIR}/prompt-${PROMPT_INDEX}-stderr.txt"
  return 1
}

# --- Main ---
main() {
  write_progress "initializing" "worker.sh started, run_id=$RUN_ID, mode=$WORKER_MODE"

  [ -f "$TASK_PACKET_FILE" ] || {
    SUMMARY_ISSUES+=("task-packet.json missing")
    write_manifest "error"
    write_summary "FAILED: task-packet.json not found"
    WORKER_CLEAN_EXIT=1
    exit 1
  }

  WORK_REQUEST_NAME=$(jq -r '.work_request_name // "unknown"' "$TASK_PACKET_FILE")
  FEEDBACK=$(jq -r '.feedback // empty' "$TASK_PACKET_FILE")

  # Preflight
  if [ "$WORKER_MODE" = "real" ]; then
    if ! command -v claude >/dev/null 2>&1; then
      SUMMARY_ISSUES+=("claude CLI not found on PATH")
      write_manifest "error"
      write_summary "FAILED: claude CLI not found on PATH"
      WORKER_CLEAN_EXIT=1
      exit 1
    fi
  fi

  # Enumerate prompts
  mapfile -t PROMPTS < <(find "$PLANS_DIR" -maxdepth 1 -name '[0-9]*-*.md' -printf '%f\n' | sort -V)
  PROMPT_TOTAL=${#PROMPTS[@]}

  if [ "$PROMPT_TOTAL" -eq 0 ]; then
    SUMMARY_ISSUES+=("no prompt files found in $PLANS_DIR")
    write_manifest "error"
    write_summary "FAILED: no prompt files found"
    WORKER_CLEAN_EXIT=1
    exit 1
  fi

  write_progress "executing" "Found $PROMPT_TOTAL prompts, starting execution"

  # Per-prompt timing log. CollectStage on the host reconstructs these into
  # OTel spans back-dated to the ISO-8601 timestamps below so the Jaeger
  # waterfall shows per-prompt detail inside workflow.stage.Dispatch instead
  # of one opaque slab.
  PROMPT_TIMING_FILE="${OUTPUT_DIR}/per-prompt-timing.jsonl"
  : > "$PROMPT_TIMING_FILE"

  # Run each prompt
  for prompt_name in "${PROMPTS[@]}"; do
    PROMPT_INDEX=$((PROMPT_INDEX + 1))
    local prompt_path="${PLANS_DIR}/${prompt_name}"

    write_progress "executing" "Running prompt $PROMPT_INDEX/$PROMPT_TOTAL: $prompt_name"

    # Millisecond-precision UTC timestamps; GNU date on Ubuntu 24.04 supports %3N.
    local prompt_start_ts prompt_start_epoch_ms
    prompt_start_ts=$(date -u '+%Y-%m-%dT%H:%M:%S.%3NZ')
    prompt_start_epoch_ms=$(($(date +%s%N) / 1000000))

    local exit_code=0
    if [ "$WORKER_MODE" = "fake" ]; then
      run_claude_fake "$prompt_path" || exit_code=$?
    elif [ "$WORKER_MODE" = "fake-bad" ]; then
      # Retry-demo behavior: if the reviewer left feedback (attempt > 1), pretend
      # we addressed it and produce clean output so the retrospective can observe
      # the "improved" second attempt. First attempt has no feedback -> synthetic
      # failure that the retrospective will flag.
      if [ -n "$FEEDBACK" ]; then
        run_claude_fake "$prompt_path" || exit_code=$?
      else
        run_claude_fake_bad "$prompt_path" || exit_code=$?
      fi
    else
      run_claude_real "$prompt_path" || exit_code=$?
    fi

    local prompt_end_ts prompt_end_epoch_ms duration_ms stdout_bytes stderr_bytes
    prompt_end_ts=$(date -u '+%Y-%m-%dT%H:%M:%S.%3NZ')
    prompt_end_epoch_ms=$(($(date +%s%N) / 1000000))
    duration_ms=$((prompt_end_epoch_ms - prompt_start_epoch_ms))
    stdout_bytes=$(stat -c '%s' "${OUTPUT_DIR}/prompt-${PROMPT_INDEX}-stdout.txt" 2>/dev/null || echo 0)
    stderr_bytes=$(stat -c '%s' "${OUTPUT_DIR}/prompt-${PROMPT_INDEX}-stderr.txt" 2>/dev/null || echo 0)

    # One JSONL line per prompt. jq builds it so the JSON is always well-formed
    # even if a filename contains spaces or quotes.
    jq -nc \
      --argjson prompt_index  "$PROMPT_INDEX" \
      --arg     filename      "$prompt_name" \
      --arg     mode          "$WORKER_MODE" \
      --arg     start_ts      "$prompt_start_ts" \
      --arg     end_ts        "$prompt_end_ts" \
      --argjson duration_ms   "$duration_ms" \
      --argjson exit_code     "$exit_code" \
      --argjson stdout_bytes  "$stdout_bytes" \
      --argjson stderr_bytes  "$stderr_bytes" \
      '{prompt_index:$prompt_index, filename:$filename, mode:$mode, start_ts:$start_ts, end_ts:$end_ts, duration_ms:$duration_ms, exit_code:$exit_code, stdout_bytes:$stdout_bytes, stderr_bytes:$stderr_bytes}' \
      >> "$PROMPT_TIMING_FILE"

    if [ "$exit_code" -eq 0 ]; then
      PROMPT_RESULTS+=("$prompt_name: success (exit=$exit_code)")
    else
      PROMPT_RESULTS+=("$prompt_name: FAILED (exit=$exit_code)")
      SUMMARY_ISSUES+=("prompt $PROMPT_INDEX ($prompt_name) failed with exit code $exit_code")
    fi

    # Write partial manifest after every prompt for SIGKILL resilience
    write_manifest "in_progress"
  done

  # Final outputs
  write_progress "writing_outputs" "All prompts done, writing final outputs"

  local succeeded=0 failed=0
  for r in "${PROMPT_RESULTS[@]}"; do
    if [[ "$r" == *"success"* ]]; then
      succeeded=$((succeeded + 1))
    else
      failed=$((failed + 1))
    fi
  done

  local desc="${WORK_REQUEST_NAME}: ${succeeded}/${PROMPT_TOTAL} prompts succeeded"
  [ "$failed" -gt 0 ] && desc="${desc}, ${failed} failed"

  write_manifest "complete"
  write_summary "$desc"

  write_progress "done" "Worker completed: $desc"

  WORKER_CLEAN_EXIT=1
  exit 0
}

main "$@"

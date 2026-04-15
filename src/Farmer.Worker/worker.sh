#!/bin/bash
#
# Farmer Worker — Phase 6 real worker
# ====================================
#
# Invoked by DispatchStage via:
#   cd ~/projects && bash worker.sh <run_id>
#
# Runs Claude CLI per prompt file in ~/projects/plans/, captures output,
# writes manifest.json + summary.json + worker-retro.md for CollectStage.
#
# Full dangerous mode: --dangerously-skip-permissions, no tool allowlist,
# high max-turns. The VM is the sandbox. See ADR-008.
#
# FARMER_WORKER_MODE=fake runs without Claude CLI (canned output).
# Default is real.
#

set -uo pipefail

# --- PATH setup (Claude installed via npm local prefix) ---
export PATH="$HOME/.npm-global/bin:$PATH"

RUN_ID="${1:-unknown}"
PROJECT_ROOT="${HOME}/projects"
PLANS_DIR="${PROJECT_ROOT}/plans"
OUTPUT_DIR="${PROJECT_ROOT}/output"
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
  # Compute files_changed from git status
  cd "$PROJECT_ROOT"
  git add -A 2>/dev/null
  files_json=$(git status --porcelain=v1 2>/dev/null | awk '{print $2}' | sort -u | jq -R -s 'split("\n") | map(select(length > 0))' 2>/dev/null)
  if [ -z "$files_json" ] || [ "$files_json" = "[]" ]; then
    files_json='["WORKER_NO_CHANGES"]'
  fi

  jq -n \
    --arg run_id "$RUN_ID" \
    --argjson files "$files_json" \
    --arg ts "$(date -u -Iseconds)" \
    --arg status "$status" \
    '{
      run_id: $run_id,
      files_changed: $files,
      branch_name: "",
      commit_sha: null,
      generated_at: $ts
    }' > "$OUTPUT_DIR/manifest.json"
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

  # Run each prompt
  for prompt_name in "${PROMPTS[@]}"; do
    PROMPT_INDEX=$((PROMPT_INDEX + 1))
    local prompt_path="${PLANS_DIR}/${prompt_name}"

    write_progress "executing" "Running prompt $PROMPT_INDEX/$PROMPT_TOTAL: $prompt_name"

    local exit_code=0
    if [ "$WORKER_MODE" = "fake" ]; then
      run_claude_fake "$prompt_path" || exit_code=$?
    else
      run_claude_real "$prompt_path" || exit_code=$?
    fi

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

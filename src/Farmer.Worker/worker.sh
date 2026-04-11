#!/bin/bash
#
# FAKE WORKER — Phase 5/6 bridge calibration script
# ===================================================
#
# This is NOT a real worker. It exists only to prove the Farmer
# orchestration pipeline runs end-to-end against a real VM with
# real SSH and a real mapped drive. It performs no actual work.
#
# A real worker.sh — the one that ships in Phase 6 — will:
#   - Read prompts from ~/projects/plans/ in numeric order
#   - Run claude --dangerously-skip-permissions per prompt
#   - Update ~/projects/.comms/progress.md throughout
#   - Create a feature branch and push it
#   - Write a real manifest.json with real files_changed
#
# This fake version does none of that. It writes a manifest with
# a single sentinel file ("FAKE_WORKER_NO_REAL_CHANGES") so anyone
# inspecting the run after the fact can immediately tell this was
# a calibration run and not a real build.
#
# When Phase 6 lands, this entire file is replaced. The path stays
# the same so DeliverStage doesn't need to change.
#
# See: docs/end-to-end-verification.md for the why and how.
#

set -euo pipefail

RUN_ID="${1:-unknown}"
PROJECT_ROOT="${HOME}/projects"

mkdir -p "${PROJECT_ROOT}/output"
mkdir -p "${PROJECT_ROOT}/.comms"

# Heartbeat — write a progress.md so the host can see us pulse
cat > "${PROJECT_ROOT}/.comms/progress.md" <<EOF
---
phase: complete
prompt: 0
total: 0
updated: $(date -u -Iseconds)
fake_worker: true
---
This is the fake calibration worker. No real work was performed.
EOF

# Manifest — minimum fields CollectStage requires
cat > "${PROJECT_ROOT}/output/manifest.json" <<EOF
{
  "run_id": "${RUN_ID}",
  "files_changed": ["FAKE_WORKER_NO_REAL_CHANGES"],
  "branch_name": "farmer-fake-worker-${RUN_ID}",
  "commit_sha": null,
  "generated_at": "$(date -u -Iseconds)"
}
EOF

# Summary — minimum fields CollectStage requires
cat > "${PROJECT_ROOT}/output/summary.json" <<EOF
{
  "run_id": "${RUN_ID}",
  "description": "Fake worker calibration run. No real work performed.",
  "issues": [],
  "retro": "Fake worker is a Phase 5/6 bridge artifact. See docs/end-to-end-verification.md.",
  "generated_at": "$(date -u -Iseconds)"
}
EOF

echo "fake worker complete for ${RUN_ID}"
exit 0

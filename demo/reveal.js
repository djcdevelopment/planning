// Stream J (Phase Demo) — reveal.js
//
// Vanilla JS, no framework. Drives demo/reveal.html against Farmer.Host's
// Stream J endpoints. Polls /runs every 5s so a retry kicked off from the
// Directives tab shows up in the sidebar without a manual refresh.
//
// API base resolution:
//   1. ?api=... query string wins
//   2. window.location.origin if not file://
//   3. http://localhost:5100 fallback
//
// Jaeger UI is assumed at http://localhost:16686; override via ?jaeger=...

(function () {
  "use strict";

  // ---- config + state ------------------------------------------------------

  const qs = new URLSearchParams(window.location.search);
  const API =
    qs.get("api") ||
    (window.location.protocol.startsWith("http") ? window.location.origin : "http://localhost:5100");
  const JAEGER = qs.get("jaeger") || "http://localhost:16686";

  document.getElementById("api-base").textContent = API;

  const state = {
    runs: [],
    selectedId: null,
    selectedDetail: null,
    pollTimer: null,
  };

  // ---- fetch helpers -------------------------------------------------------

  async function api(path) {
    const res = await fetch(API + path, { headers: { Accept: "application/json" } });
    if (!res.ok) throw new Error(path + " -> HTTP " + res.status);
    const ct = res.headers.get("content-type") || "";
    return ct.includes("json") ? res.json() : res.text();
  }

  async function fetchFileText(runId, relPath) {
    const url = API + "/runs/" + encodeURIComponent(runId) + "/file/" + relPath
      .split("/")
      .map(encodeURIComponent)
      .join("/");
    const res = await fetch(url);
    if (!res.ok) throw new Error(url + " -> HTTP " + res.status);
    return res.text();
  }

  // ---- sidebar -------------------------------------------------------------

  async function refreshRuns() {
    try {
      const runs = await api("/runs");
      state.runs = Array.isArray(runs) ? runs : [];
      renderSidebar();
    } catch (e) {
      console.warn("refreshRuns failed:", e);
      document.getElementById("run-list").innerHTML =
        '<li class="run-list-empty">Cannot reach ' + API + '. Is Farmer.Host running?</li>';
    }
  }

  function renderSidebar() {
    const list = document.getElementById("run-list");
    document.getElementById("run-count").textContent = state.runs.length;

    if (state.runs.length === 0) {
      list.innerHTML = '<li class="run-list-empty">No runs yet.</li>';
      return;
    }

    list.innerHTML = "";
    for (const run of state.runs) {
      const li = document.createElement("li");
      li.className = "run-row" + (run.runId === state.selectedId ? " active" : "");
      li.dataset.runId = run.runId;

      const title = document.createElement("div");
      title.className = "run-row-title";
      title.textContent = run.workRequestName || "(unnamed work request)";

      const meta = document.createElement("div");
      meta.className = "run-row-meta";

      const verdict = document.createElement("span");
      verdict.className = "verdict-badge " + verdictClass(run.verdict);
      verdict.textContent = run.verdict || "n/a";

      const duration = document.createElement("span");
      duration.textContent = formatDuration(run.durationSeconds);

      meta.appendChild(verdict);
      meta.appendChild(duration);

      const idEl = document.createElement("div");
      idEl.className = "run-row-id";
      idEl.textContent = run.runId;

      li.appendChild(title);
      li.appendChild(meta);
      li.appendChild(idEl);

      li.addEventListener("click", () => selectRun(run.runId));
      list.appendChild(li);
    }
  }

  function verdictClass(v) {
    if (!v) return "";
    const s = String(v).toLowerCase();
    if (s === "accept") return "accept";
    if (s === "retry") return "retry";
    if (s === "reject") return "reject";
    return "";
  }

  function formatDuration(secs) {
    if (secs == null) return "—";
    if (secs < 60) return secs.toFixed(1) + "s";
    const m = Math.floor(secs / 60);
    const s = Math.round(secs - m * 60);
    return m + "m" + String(s).padStart(2, "0") + "s";
  }

  // ---- run detail ----------------------------------------------------------

  async function selectRun(runId) {
    state.selectedId = runId;
    state.selectedDetail = null;
    document.getElementById("empty-state").classList.add("hidden");
    document.getElementById("run-detail").classList.remove("hidden");

    document.querySelectorAll(".run-row").forEach((el) => {
      el.classList.toggle("active", el.dataset.runId === runId);
    });

    try {
      const detail = await api("/runs/" + encodeURIComponent(runId));
      state.selectedDetail = detail;
      renderDetail(detail);
    } catch (e) {
      toast("Failed to load run: " + e.message, "error");
    }
  }

  function renderDetail(detail) {
    const s = detail.summary;

    document.getElementById("rd-title").textContent = s.workRequestName || "(unnamed work request)";

    const verdictEl = document.getElementById("rd-verdict");
    verdictEl.className = "verdict-badge " + verdictClass(s.verdict);
    verdictEl.textContent = s.verdict || "n/a";

    document.getElementById("rd-risk").textContent = "risk: " + (s.riskScore != null ? s.riskScore : "—");
    document.getElementById("rd-duration").textContent = "duration: " + formatDuration(s.durationSeconds);
    document.getElementById("rd-stages").textContent = "phase: " + (s.finalPhase || "—");
    document.getElementById("rd-run-id").textContent = s.runId;
    document.getElementById("rd-path").textContent = detail.runDir || "";

    renderTraceTab(detail);
    renderArtifactsTab(detail);
    renderRetroTab(detail);
    renderDirectivesTab(detail);
  }

  function renderTraceTab(detail) {
    const iframe = document.getElementById("trace-iframe");
    const missing = document.getElementById("trace-missing");
    if (detail.traceId) {
      iframe.src = JAEGER + "/trace/" + encodeURIComponent(detail.traceId);
      iframe.classList.remove("hidden");
      missing.classList.add("hidden");
    } else {
      iframe.src = "about:blank";
      iframe.classList.add("hidden");
      missing.classList.remove("hidden");
      document.getElementById("trace-jaeger-link").href = JAEGER;
    }
  }

  function renderArtifactsTab(detail) {
    const list = document.getElementById("artifact-list");
    const sourceLabel = document.getElementById("artifacts-source");
    list.innerHTML = "";

    const art = detail.artifacts || { source: "none", entries: [] };
    sourceLabel.textContent =
      "source: " + art.source + " · " + (art.entries ? art.entries.length : 0) + " entries";

    if (!art.entries || art.entries.length === 0) {
      const li = document.createElement("li");
      li.className = "artifact-row";
      li.textContent = "(no artifacts captured for this run)";
      list.appendChild(li);
      return;
    }

    for (const entry of art.entries) {
      const li = document.createElement("li");
      li.className = "artifact-row";

      const header = document.createElement("div");
      header.className = "artifact-row-header";

      const pathEl = document.createElement("span");
      pathEl.className = "artifact-path";
      pathEl.textContent = entry.path;

      const metaEl = document.createElement("span");
      metaEl.className = "artifact-meta";
      if (entry.status && entry.status !== "captured") {
        metaEl.classList.add("artifact-status-" + entry.status);
      }
      const bytesLabel = entry.bytes != null ? entry.bytes + " B" : "—";
      metaEl.textContent = (entry.status || "captured") + " · " + bytesLabel;

      header.appendChild(pathEl);
      header.appendChild(metaEl);
      li.appendChild(header);

      if (entry.reason || entry.detail) {
        const det = document.createElement("div");
        det.className = "artifact-detail";
        det.textContent = [entry.reason, entry.detail].filter(Boolean).join(" — ");
        li.appendChild(det);
      }

      if (entry.status === "captured" || entry.status === null || entry.status === undefined) {
        const content = document.createElement("pre");
        content.className = "artifact-content hidden";
        li.appendChild(content);

        header.addEventListener("click", async () => {
          if (!content.classList.contains("hidden")) {
            content.classList.add("hidden");
            return;
          }
          content.classList.remove("hidden");
          if (content.dataset.loaded !== "1") {
            content.textContent = "Loading...";
            try {
              const text = await fetchFileText(detail.summary.runId, "artifacts/" + entry.path);
              content.textContent = text;
              content.dataset.loaded = "1";
            } catch (e) {
              content.textContent = "Failed: " + e.message;
            }
          }
        });
      }

      list.appendChild(li);
    }
  }

  async function renderRetroTab(detail) {
    document.getElementById("retro-md").textContent = detail.qaRetroMarkdown || "(no qa-retro.md)";

    const summaryEl = document.getElementById("review-summary");
    const rawEl = document.getElementById("review-raw");
    summaryEl.innerHTML = "";
    rawEl.textContent = "Loading...";

    try {
      const reviewText = await fetchFileText(detail.summary.runId, "review.json");
      rawEl.textContent = reviewText;

      try {
        const review = JSON.parse(reviewText);
        summaryEl.innerHTML =
          '<div><strong>Verdict:</strong> <span class="verdict-badge ' + verdictClass(review.verdict) + '">' +
            (review.verdict || "—") +
          '</span> · <strong>Risk:</strong> ' + (review.risk_score != null ? review.risk_score : "—") + "</div>" +
          (review.findings && review.findings.length ? '<h4>Findings</h4><ul class="findings">' +
            review.findings.map((f) => '<li>' + escapeHtml(f) + '</li>').join("") + "</ul>" : "") +
          (review.suggestions && review.suggestions.length ? '<h4>Suggestions</h4><ul class="suggestions">' +
            review.suggestions.map((f) => '<li>' + escapeHtml(f) + '</li>').join("") + "</ul>" : "");
      } catch {
        summaryEl.innerHTML = '<em>review.json present but not valid JSON</em>';
      }
    } catch {
      rawEl.textContent = "(no review.json)";
      summaryEl.innerHTML = '<em>No review.json for this run.</em>';
    }
  }

  function renderDirectivesTab(detail) {
    const container = document.getElementById("directive-cards");
    const empty = document.getElementById("directive-empty");
    container.innerHTML = "";

    const md = detail.directivesMarkdown;
    if (!md || !md.trim()) {
      empty.classList.remove("hidden");
      return;
    }
    empty.classList.add("hidden");

    const cards = splitDirectives(md);
    if (cards.length === 0) {
      // One big card when we can't split -- still useful.
      container.appendChild(buildDirectiveCard("Directive", md, detail));
      return;
    }
    for (const c of cards) {
      container.appendChild(buildDirectiveCard(c.title, c.body, detail));
    }
  }

  // Splits directive-suggestions.md on numbered headings ("## 1. ..." / "## 2. ...").
  // Each section becomes a card. Falls back to a single card if the document is flat.
  function splitDirectives(md) {
    const lines = md.split(/\r?\n/);
    const sections = [];
    let current = null;
    for (const line of lines) {
      const m = /^##\s+(\d+[\.\)]?\s+.+)$/.exec(line);
      if (m) {
        if (current) sections.push(current);
        current = { title: m[1].trim(), body: "" };
      } else if (current) {
        current.body += line + "\n";
      }
    }
    if (current) sections.push(current);
    return sections.map((s) => ({ title: s.title, body: s.body.trim() }));
  }

  function buildDirectiveCard(title, body, detail) {
    const card = document.createElement("div");
    card.className = "directive-card";

    const h3 = document.createElement("h3");
    h3.textContent = title;
    card.appendChild(h3);

    const pre = document.createElement("pre");
    pre.className = "directive-body";
    pre.textContent = body;
    card.appendChild(pre);

    const actions = document.createElement("div");
    actions.className = "directive-card-actions";

    const btn = document.createElement("button");
    btn.className = "btn-primary";
    btn.textContent = "Apply + re-run";
    btn.addEventListener("click", () => applyDirective(btn, title, body, detail));

    const help = document.createElement("span");
    help.className = "panel-hint";
    help.style.margin = "0";
    help.textContent = "POST /trigger with prompts_inline including 0-feedback.md";

    actions.appendChild(btn);
    actions.appendChild(help);
    card.appendChild(actions);

    return card;
  }

  async function applyDirective(btn, title, body, detail) {
    btn.disabled = true;
    btn.textContent = "Triggering...";

    // Build the feedback payload. We include the original work_request_name +
    // parent_run_id so the new run threads back to this one. prompts_inline
    // carries a single 0-feedback.md + a note pointing at the parent run —
    // Stream H is wiring prompts_inline to skip the disk sample-plan lookup,
    // but the parent-id note keeps the provenance discoverable if Stream H's
    // wiring changes.
    const feedbackContent =
      "# Feedback directive applied from " + detail.summary.runId + "\n\n" +
      "## " + title + "\n\n" +
      body + "\n";

    const payload = {
      work_request_name: detail.summary.workRequestName || "demo-rerun",
      worker_mode: "real",
      prompts_inline: [
        { filename: "0-feedback.md", content: feedbackContent },
        {
          filename: "99-parent-note.md",
          content:
            "# Parent run\n\nThis run was triggered from a directive in `" + detail.summary.runId + "`.\n" +
            "See its artifacts + retro under `runs/" + detail.summary.runId + "/`.\n",
        },
      ],
      parent_run_id: detail.summary.runId,
    };

    try {
      const res = await fetch(API + "/trigger", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
      const text = await res.text();
      if (!res.ok) throw new Error("HTTP " + res.status + ": " + text);
      toast("Re-run triggered. It'll show up in the sidebar shortly.", "success");
      // Kick the sidebar so the new run shows up ASAP.
      refreshRuns();
    } catch (e) {
      toast("Trigger failed: " + e.message, "error");
    } finally {
      btn.disabled = false;
      btn.textContent = "Apply + re-run";
    }
  }

  // ---- tabs ----------------------------------------------------------------

  document.querySelectorAll(".tab").forEach((tab) => {
    tab.addEventListener("click", () => {
      const name = tab.dataset.tab;
      document.querySelectorAll(".tab").forEach((t) => t.classList.toggle("active", t === tab));
      document
        .querySelectorAll(".tab-panel")
        .forEach((p) => p.classList.toggle("active", p.dataset.panel === name));
    });
  });

  // ---- polling -------------------------------------------------------------

  function startPolling() {
    stopPolling();
    state.pollTimer = setInterval(() => {
      if (document.getElementById("auto-refresh").checked) refreshRuns();
    }, 5000);
  }
  function stopPolling() {
    if (state.pollTimer) clearInterval(state.pollTimer);
    state.pollTimer = null;
  }
  document.getElementById("refresh-now").addEventListener("click", refreshRuns);

  // ---- toast ---------------------------------------------------------------

  let toastTimer = null;
  function toast(msg, kind) {
    const el = document.getElementById("toast");
    el.textContent = msg;
    el.className = "toast " + (kind || "");
    el.classList.remove("hidden");
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.classList.add("hidden"), 5000);
  }

  function escapeHtml(s) {
    return String(s)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  // ---- boot ----------------------------------------------------------------

  refreshRuns().then(startPolling);
})();

const runList = document.getElementById("run-list");
const detail = document.getElementById("detail");
const form = document.getElementById("run-form");
const promptInput = document.getElementById("prompt-input");
const runButton = document.getElementById("run-button");

let activeRunId = null;

async function fetchRuns() {
  const res = await fetch("/agent/runs");
  return res.json();
}

async function fetchRun(runId) {
  const res = await fetch(`/agent/runs/${runId}`);
  if (!res.ok) return null;
  return res.json();
}

function formatDate(iso) {
  return new Date(iso).toLocaleString("fr-FR");
}

function renderRunList(runs) {
  runList.innerHTML = "";

  if (runs.length === 0) {
    runList.innerHTML = '<p class="empty">Aucun run pour l\'instant.</p>';
    return;
  }

  for (const run of runs) {
    const item = document.createElement("div");
    item.className = "run-item" + (run.runId === activeRunId ? " active" : "") + (run.hasPiiViolation ? " pii-violation" : "");
    const piiBadge = run.hasPiiViolation ? '<span class="pii-badge">⚠ PII</span>' : "";
    item.innerHTML = `
      <div class="prompt">${escapeHtml(run.prompt)} ${piiBadge}</div>
      <div class="meta">${formatDate(run.startedAt)} · ${run.totalDurationMs} ms · ${run.totalInputTokens + run.totalOutputTokens} tokens</div>
    `;
    item.addEventListener("click", () => selectRun(run.runId));
    runList.appendChild(item);
  }
}

function escapeHtml(str) {
  const div = document.createElement("div");
  div.textContent = str;
  return div.innerHTML;
}

function renderDetail(trace) {
  const maxDuration = Math.max(...trace.steps.map((s) => s.durationMs), 1);

  const stepsHtml = trace.steps
    .map((step) => {
      // `kind` est sérialisé en texte ("ModelCall" / "ToolCall" / "PolicyDenial"),
      // pas en entier — la comparaison numérique d'origine était toujours fausse.
      const cls =
        step.kind === "ModelCall" ? "model" : step.kind === "PolicyDenial" ? "denial" : "tool";
      const tokens =
        step.inputTokens != null || step.outputTokens != null
          ? `<span>· tokens in=${step.inputTokens ?? 0} out=${step.outputTokens ?? 0}</span>`
          : "";
      const widthPct = Math.max((step.durationMs / maxDuration) * 100, 3);

      return `
        <div class="step ${cls === "denial" ? "step-denial" : ""}">
          <div class="step-head">
            <span class="step-title ${cls}">[${step.index}] ${escapeHtml(step.label)}</span>
            <span class="step-duration">${step.durationMs} ms ${tokens}</span>
          </div>
          <div class="step-detail">${escapeHtml(step.detail)}</div>
          <div class="bar-track"><div class="bar-fill ${cls}" style="width:${widthPct}%"></div></div>
        </div>
      `;
    })
    .join("");

  const piiBanner = trace.hasPiiViolation
    ? `<div class="pii-violation-banner">
        <div class="label">⚠ Violation PII détectée</div>
        ${Object.entries(trace.piiFindingsByType)
          .map(([type, count]) => `<span class="pii-chip">${escapeHtml(type)} × ${count}</span>`)
          .join(" ")}
      </div>`
    : "";

  detail.innerHTML = `
    <div class="stat-row">
      <div class="stat"><div class="label">Durée totale</div><div class="value">${trace.totalDurationMs} ms</div></div>
      <div class="stat"><div class="label">Tokens in</div><div class="value">${trace.totalInputTokens}</div></div>
      <div class="stat"><div class="label">Tokens out</div><div class="value">${trace.totalOutputTokens}</div></div>
      <div class="stat"><div class="label">Coût estimé</div><div class="value">${trace.estimatedCostEur.toFixed(4)} €</div></div>
    </div>
    ${piiBanner}
    <div class="timeline">${stepsHtml}</div>
    <div class="final-answer">
      <div class="label">Réponse finale</div>
      ${escapeHtml(trace.finalAnswer)}
    </div>
  `;
}

async function selectRun(runId) {
  activeRunId = runId;
  const runs = await fetchRuns();
  renderRunList(runs);

  detail.innerHTML = '<p class="empty">Chargement…</p>';
  const trace = await fetchRun(runId);
  if (trace) renderDetail(trace);
}

async function refreshRunList() {
  const runs = await fetchRuns();
  renderRunList(runs);
}

form.addEventListener("submit", async (e) => {
  e.preventDefault();
  const prompt = promptInput.value.trim();
  if (!prompt) return;

  runButton.disabled = true;
  runButton.textContent = "Run en cours…";
  detail.innerHTML = '<p class="empty">Agent en cours d\'exécution (peut prendre quelques secondes)…</p>';

  try {
    const res = await fetch("/agent/run", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ prompt }),
    });
    const trace = await res.json();
    promptInput.value = "";
    await selectRun(trace.runId);
  } catch (err) {
    detail.innerHTML = `<p class="empty">Erreur: ${escapeHtml(String(err))}</p>`;
  } finally {
    runButton.disabled = false;
    runButton.textContent = "Lancer un run";
  }
});

refreshRunList();

// ===== Utilitaires =====

function escapeHtml(str) {
  const div = document.createElement("div");
  div.textContent = str ?? "";
  return div.innerHTML;
}

function formatDate(iso) {
  return new Date(iso).toLocaleString("fr-FR");
}

async function fetchJson(url, options) {
  const res = await fetch(url, options);
  if (!res.ok) throw new Error(`${res.status} sur ${url}`);
  return res.json();
}

// ===== Onglets =====

const tabButtons = document.querySelectorAll("nav.tabs button");
const loadedTabs = new Set(["runs"]);

tabButtons.forEach((btn) => {
  btn.addEventListener("click", () => {
    tabButtons.forEach((b) => b.classList.toggle("active", b === btn));
    document.querySelectorAll(".tab-panel").forEach((p) => p.classList.remove("active"));
    document.getElementById(`tab-${btn.dataset.tab}`).classList.add("active");

    // Chargement paresseux : chaque onglet ne charge ses données qu'à la première ouverture.
    if (!loadedTabs.has(btn.dataset.tab)) {
      loadedTabs.add(btn.dataset.tab);
      if (btn.dataset.tab === "evals") refreshEvals();
      if (btn.dataset.tab === "audit") refreshAudit();
      if (btn.dataset.tab === "external") refreshExternalScans();
    }
  });
});

function switchToTab(name) {
  document.querySelector(`nav.tabs button[data-tab="${name}"]`).click();
}

// ===== Budget (en-tête, toujours visible) =====

const budgetWidget = document.getElementById("budget-widget");

async function refreshBudget() {
  try {
    const b = await fetchJson("/budget/status");
    const pct = Math.min(b.periodUsagePercent, 100);
    const cls = b.hasBreach ? "over" : b.periodUsagePercent >= 75 ? "warn" : "";
    budgetWidget.classList.toggle("breach", b.hasBreach);
    budgetWidget.innerHTML = `
      <div class="budget-line">Budget tokens ${b.periodHours} h —
        <span class="budget-value">${b.periodTokensUsed.toLocaleString("fr-FR")} / ${b.periodTokenBudget.toLocaleString("fr-FR")} (${b.periodUsagePercent} %)</span>
        ${b.hasBreach ? " ⚠" : ""}
      </div>
      <div class="budget-track"><div class="budget-fill ${cls}" style="width:${pct}%"></div></div>
    `;
  } catch {
    budgetWidget.innerHTML = `<div class="budget-line">Budget indisponible</div>`;
  }
}

// ===== Onglet Runs (logique d'origine) =====

const runList = document.getElementById("run-list");
const detail = document.getElementById("detail");
const form = document.getElementById("run-form");
const promptInput = document.getElementById("prompt-input");
const runButton = document.getElementById("run-button");

let activeRunId = null;

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

function renderDetail(trace) {
  const maxDuration = Math.max(...trace.steps.map((s) => s.durationMs), 1);

  const stepsHtml = trace.steps
    .map((step) => {
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
  const runs = await fetchJson("/agent/runs");
  renderRunList(runs);

  detail.innerHTML = '<p class="empty">Chargement…</p>';
  try {
    renderDetail(await fetchJson(`/agent/runs/${runId}`));
  } catch {
    detail.innerHTML = '<p class="empty">Run introuvable.</p>';
  }
}

async function refreshRunList() {
  renderRunList(await fetchJson("/agent/runs"));
}

form.addEventListener("submit", async (e) => {
  e.preventDefault();
  const prompt = promptInput.value.trim();
  if (!prompt) return;

  runButton.disabled = true;
  runButton.textContent = "Run en cours…";
  detail.innerHTML = '<p class="empty">Agent en cours d\'exécution (peut prendre quelques secondes)…</p>';

  try {
    const trace = await fetchJson("/agent/run", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ prompt }),
    });
    promptInput.value = "";
    await selectRun(trace.runId);
    refreshBudget(); // le run vient de consommer des tokens
  } catch (err) {
    detail.innerHTML = `<p class="empty">Erreur: ${escapeHtml(String(err))}</p>`;
  } finally {
    runButton.disabled = false;
    runButton.textContent = "Lancer un run";
  }
});

// ===== Onglet Évals =====

const runEvalsButton = document.getElementById("run-evals-button");
const evalsRegression = document.getElementById("evals-regression");
const evalsList = document.getElementById("evals-list");
const evalsDetail = document.getElementById("evals-detail");

function renderEvalsList(summaries) {
  if (summaries.length === 0) {
    evalsList.innerHTML = '<p class="empty">Aucun run d\'évals — lance le premier.</p>';
    return;
  }

  evalsList.innerHTML = `
    <table class="data">
      <thead><tr><th>Date</th><th>Modèle</th><th>Cas</th><th>Score</th></tr></thead>
      <tbody>
        ${summaries.map((s) => `
          <tr class="clickable" data-id="${s.evalRunId}">
            <td>${formatDate(s.startedAt)}</td>
            <td>${escapeHtml(s.modelName)}</td>
            <td>${s.casesPassed}/${s.casesTotal}</td>
            <td><span class="badge ${s.scorePercent === 100 ? "pass" : "fail"}">${s.scorePercent} %</span></td>
          </tr>`).join("")}
      </tbody>
    </table>
  `;

  evalsList.querySelectorAll("tr.clickable").forEach((row) => {
    row.addEventListener("click", () => showEvalDetail(row.dataset.id));
  });
}

function renderEvalReport(report) {
  evalsDetail.innerHTML = `
    <h3 style="font-size:0.95rem; margin: 1.5rem 0 0.75rem;">Détail du run du ${formatDate(report.startedAt)}</h3>
    ${report.caseResults.map((c) => `
      <div class="eval-case ${c.passed ? "" : "failed"}">
        <div class="head">
          <span>${c.passed ? "✅" : "❌"} ${escapeHtml(c.caseId)}</span>
          <span class="step-duration">${c.durationMs} ms · ${c.totalTokens} tokens</span>
        </div>
        ${c.assertionResults.map((a) => `
          <div class="assertion ${a.passed ? "" : "failed"}">
            ${a.passed ? "✓" : "✗"} ${escapeHtml(a.assertion.kind)} — ${escapeHtml(a.detail)}
          </div>`).join("")}
      </div>`).join("")}
  `;
}

function renderRegression(regression) {
  if (!regression) {
    evalsRegression.innerHTML = "";
    return;
  }

  const cls = regression.isRegression ? "bad" : "ok";
  const title = regression.isRegression
    ? `⚠ Régression détectée (${regression.scoreDelta > 0 ? "+" : ""}${regression.scoreDelta} pts vs run précédent)`
    : `Pas de régression (${regression.scoreDelta > 0 ? "+" : ""}${regression.scoreDelta} pts vs run précédent)`;

  const parts = [];
  if (regression.newlyFailingCaseIds.length > 0) parts.push(`cas cassés : ${regression.newlyFailingCaseIds.join(", ")}`);
  if (regression.newlyPassingCaseIds.length > 0) parts.push(`cas réparés : ${regression.newlyPassingCaseIds.join(", ")}`);

  evalsRegression.innerHTML = `
    <div class="banner ${cls}">
      <div class="title">${title}</div>
      ${parts.length ? `<div class="sub">${escapeHtml(parts.join(" · "))}</div>` : ""}
    </div>
  `;
}

async function refreshEvals() {
  try {
    renderEvalsList(await fetchJson("/evals/runs"));
  } catch {
    evalsList.innerHTML = '<p class="empty">Impossible de charger les évals.</p>';
  }
}

async function showEvalDetail(evalRunId) {
  evalsDetail.innerHTML = '<p class="empty">Chargement…</p>';
  renderEvalReport(await fetchJson(`/evals/runs/${evalRunId}`));
}

runEvalsButton.addEventListener("click", async () => {
  runEvalsButton.disabled = true;
  runEvalsButton.textContent = "Évals en cours… (~1 min)";
  evalsDetail.innerHTML = "";

  try {
    const report = await fetchJson("/evals/run", { method: "POST" });
    renderRegression(report.regression);
    await refreshEvals();
    renderEvalReport(report);
    refreshBudget(); // les évals consomment des tokens
  } catch (err) {
    evalsRegression.innerHTML = `<div class="banner bad"><div class="title">Échec du run d'évals</div><div class="sub">${escapeHtml(String(err))}</div></div>`;
  } finally {
    runEvalsButton.disabled = false;
    runEvalsButton.textContent = "Lancer les évals";
  }
});

// ===== Onglet Audit =====

const verifyChainButton = document.getElementById("verify-chain-button");
const auditVerification = document.getElementById("audit-verification");
const auditEntries = document.getElementById("audit-entries");

function renderAuditEntries(entries) {
  if (entries.length === 0) {
    auditEntries.innerHTML = '<p class="empty">Journal vide.</p>';
    return;
  }

  auditEntries.innerHTML = `
    <table class="data">
      <thead><tr><th>#</th><th>Horodatage</th><th>Acteur</th><th>Action</th><th>Détails</th><th>Hash</th></tr></thead>
      <tbody>
        ${entries.map((e) => `
          <tr>
            <td>${e.sequence}</td>
            <td>${formatDate(e.timestamp)}</td>
            <td>${escapeHtml(e.actorType)}/${escapeHtml(e.actorId)}</td>
            <td><span class="badge kind">${escapeHtml(e.action)}</span></td>
            <td>${escapeHtml((e.details ?? "").slice(0, 90))}${(e.details ?? "").length > 90 ? "…" : ""}</td>
            <td class="mono">${e.hash.slice(0, 12)}…</td>
          </tr>`).join("")}
      </tbody>
    </table>
  `;
}

async function refreshAudit() {
  try {
    // Journal affiché du plus récent au plus ancien — l'ordre de lecture d'un opérateur.
    const entries = await fetchJson("/audit/entries");
    renderAuditEntries(entries.reverse());
  } catch {
    auditEntries.innerHTML = '<p class="empty">Impossible de charger le journal.</p>';
  }
}

verifyChainButton.addEventListener("click", async () => {
  verifyChainButton.disabled = true;
  auditVerification.innerHTML = '<p class="empty">Vérification…</p>';

  try {
    const v = await fetchJson("/audit/verify");
    auditVerification.innerHTML = v.isIntact
      ? `<div class="banner ok"><div class="title">✅ Chaîne intacte</div><div class="sub">${v.entriesChecked} entrée(s) vérifiée(s) — aucun signe de falsification.</div></div>`
      : `<div class="banner bad"><div class="title">❌ Intégrité compromise (séquence ${v.firstBrokenSequence})</div><div class="sub">${escapeHtml(v.explanation)}</div></div>`;
  } catch (err) {
    auditVerification.innerHTML = `<div class="banner bad"><div class="title">Vérification impossible</div><div class="sub">${escapeHtml(String(err))}</div></div>`;
  } finally {
    verifyChainButton.disabled = false;
  }
});

// ===== Onglet Recherche =====

const searchForm = document.getElementById("search-form");
const searchInput = document.getElementById("search-input");
const searchButton = document.getElementById("search-button");
const searchResults = document.getElementById("search-results");
const indexButton = document.getElementById("index-button");

const chunkKindLabels = {
  Prompt: "demande",
  Answer: "réponse",
  ToolCall: "appel d'outil",
  PolicyDenial: "refus de politique",
  GuardrailViolation: "violation PII",
};

searchForm.addEventListener("submit", async (e) => {
  e.preventDefault();
  const q = searchInput.value.trim();
  if (!q) return;

  searchButton.disabled = true;
  searchResults.innerHTML = '<p class="empty">Recherche…</p>';

  try {
    const hits = await fetchJson(`/search?q=${encodeURIComponent(q)}&limit=8`);

    if (hits.length === 0) {
      searchResults.innerHTML = '<p class="empty">Aucun résultat — les runs sont-ils indexés ?</p>';
      return;
    }

    searchResults.innerHTML = hits.map((h) => `
      <div class="search-hit" data-run="${h.runId}">
        <div class="head">
          <span>${escapeHtml(h.prompt)} ${h.hasPiiViolation ? '<span class="pii-badge">⚠ PII</span>' : ""}</span>
          <span class="similarity">${h.similarityPercent} %</span>
        </div>
        <div class="matched">
          <span class="badge kind">${chunkKindLabels[h.matchedChunkKind] ?? escapeHtml(h.matchedChunkKind)}</span>
          ${escapeHtml(h.matchedText)}
        </div>
      </div>`).join("");

    // Cliquer sur un résultat ouvre le run dans l'onglet Runs.
    searchResults.querySelectorAll(".search-hit").forEach((el) => {
      el.addEventListener("click", () => {
        switchToTab("runs");
        selectRun(el.dataset.run);
      });
    });
  } catch (err) {
    searchResults.innerHTML = `<p class="empty">Erreur : ${escapeHtml(String(err))}</p>`;
  } finally {
    searchButton.disabled = false;
  }
});

indexButton.addEventListener("click", async () => {
  indexButton.disabled = true;
  indexButton.textContent = "Indexation…";
  try {
    const report = await fetchJson("/search/index", { method: "POST" });
    indexButton.textContent = `${report.runsIndexed} indexé(s)`;
    setTimeout(() => { indexButton.textContent = "Indexer les runs"; indexButton.disabled = false; }, 2500);
  } catch {
    indexButton.textContent = "Échec";
    setTimeout(() => { indexButton.textContent = "Indexer les runs"; indexButton.disabled = false; }, 2500);
  }
});

// ===== Onglet Scans externes =====

const externalList = document.getElementById("external-list");
const refreshExternalButton = document.getElementById("refresh-external-button");

const scanKindLabels = {
  Pii: "PII",
  SchemaValidation: "Schéma",
  InjectionDetection: "Injection",
};

/// Résumé lisible du verdict, selon le type de scan.
function describeScanSummary(kind, summary) {
  if (kind === "Pii") {
    const types = Object.entries(summary.findingsByType ?? {});
    return types.length === 0
      ? "aucune PII détectée"
      : types.map(([t, n]) => `${t} × ${n}`).join(", ");
  }

  if (kind === "SchemaValidation") {
    if (summary.isValid) return "sortie conforme au schéma";
    if (summary.parseError) return `échec de parsing : ${summary.parseError}`;
    return `violations sur : ${(summary.violationPaths ?? []).join(", ") || "racine"}`;
  }

  if (kind === "InjectionDetection") {
    const signals = Object.entries(summary.signalKinds ?? {});
    const detail = signals.length ? ` — ${signals.map(([k, n]) => `${k} × ${n}`).join(", ")}` : "";
    return `risque ${summary.riskLevel} (score ${summary.score})${detail}`;
  }

  return JSON.stringify(summary);
}

function renderExternalScans(scans) {
  if (scans.length === 0) {
    externalList.innerHTML = '<p class="empty">Aucun scan externe pour l\'instant — appelle /scan, /validate ou /detect-injection depuis un système tiers.</p>';
    return;
  }

  externalList.innerHTML = `
    <table class="data">
      <thead><tr><th>Date</th><th>Type</th><th>Source</th><th>Verdict</th><th>Résumé</th></tr></thead>
      <tbody>
        ${scans.map((s) => `
          <tr>
            <td>${formatDate(s.timestamp)}</td>
            <td><span class="badge kind">${scanKindLabels[s.kind] ?? escapeHtml(s.kind)}</span></td>
            <td>${s.source ? escapeHtml(s.source) : '<span class="mono">—</span>'}</td>
            <td><span class="badge ${s.hasViolation ? "fail" : "pass"}">${s.hasViolation ? "violation" : "ok"}</span></td>
            <td>${escapeHtml(describeScanSummary(s.kind, s.summary))}</td>
          </tr>`).join("")}
      </tbody>
    </table>
  `;
}

async function refreshExternalScans() {
  try {
    renderExternalScans(await fetchJson("/external-scans"));
  } catch {
    externalList.innerHTML = '<p class="empty">Impossible de charger les scans externes.</p>';
  }
}

refreshExternalButton.addEventListener("click", refreshExternalScans);

// ===== Démarrage =====

refreshRunList();
refreshBudget();

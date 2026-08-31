const state = {
  controlToken: "",
  dashboard: null,
  sessions: [],
  models: [],
  usage: null,
  usageAnalysis: null,
  rankings: null,
  dailyProjects: null,
  skills: null,
  skillDetail: null,
  rankingMode: "days",
  projectRankingRange: "daily",
  selectedProjectDate: null,
  expandedProjectKeys: new Set(),
  projectExpansionDate: null,
  selectedUsageDate: null,
  settings: null,
  startup: null,
  pricing: null,
  selectedSessionId: null,
  sessionQuestions: null,
  sessionQuestionsLoading: false,
  sessionQuestionsError: "",
  annotationEditing: false,
  sessionTokenSort: null,
  view: "overview"
};

const privacyMask = new URLSearchParams(window.location.search).get("mask") === "1";

const viewLabels = {
  overview: "当前态势",
  usage: "用量统计",
  sessions: "会话账本",
  models: "模型对比",
  rankings: "消耗排行",
  skills: "Skills 归因",
  settings: "监控设置"
};

const pageHeadlines = {
  overview: "今天的消耗，先看压力",
  usage: "每天用了多少，一眼看清",
  sessions: "每个会话，各算各的",
  models: "同一口径，对比模型消耗",
  rankings: "消耗集中在哪里",
  skills: "每个 skill，到底带来了多少处理量",
  settings: "监控范围，由你掌控"
};

const taskTypeLabels = {
  unclassified: "未分类",
  development: "开发",
  debugging: "调试",
  research: "研究",
  documentation: "文档",
  planning: "规划",
  other: "其他"
};

const outcomeLabels = {
  unmarked: "未标记",
  completed: "已完成",
  partial: "部分完成",
  rework: "需要返工",
  failed: "失败",
  abandoned: "已放弃"
};

const reworkReasonLabels = {
  requirement_change: "需求变化",
  model_misunderstanding: "模型理解偏差",
  test_defect: "测试发现缺陷",
  environment_issue: "环境问题",
  other: "其他"
};

const $ = (selector) => document.querySelector(selector);
const $$ = (selector) => [...document.querySelectorAll(selector)];

async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  if (options.body) headers.set("Content-Type", "application/json");
  if (options.method && options.method !== "GET") headers.set("X-Monitor-Control", state.controlToken);
  const response = await fetch(path, { ...options, headers, cache: "no-store" });
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
  return response.status === 204 ? null : response.json();
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function formatExact(value) {
  return Number(value || 0).toLocaleString("zh-CN");
}

function formatTokens(value) {
  const number = Number(value || 0);
  const absolute = Math.abs(number);
  const sign = number < 0 ? "-" : "";
  if (absolute >= 100_000_000) return `${sign}${(absolute / 100_000_000).toFixed(2)} 亿`;
  if (absolute >= 10_000) return `${sign}${(absolute / 10_000).toFixed(absolute >= 1_000_000 ? 1 : 2)} 万`;
  return formatExact(number);
}

function formatUsd(value) {
  const amount = Number(value || 0);
  const digits = amount > 0 && amount < 0.01 ? 4 : 2;
  return `$${amount.toLocaleString("en-US", { minimumFractionDigits: digits, maximumFractionDigits: digits })}`;
}

function formatTime(value) {
  if (!value) return "未知";
  return new Intl.DateTimeFormat("zh-CN", { month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit" }).format(new Date(value));
}

function pressureClass(value) {
  if (value >= 95) return "danger";
  if (value >= 85) return "high";
  if (value >= 70) return "warn";
  return "calm";
}

function pressureMessage(value) {
  if (value == null) return "当前事件没有可用的上下文窗口数据。";
  if (value >= 95) return "压力很高，建议立即准备续接提示词并安全收尾。";
  if (value >= 85) return "已进入高压区，建议开始整理剩余工作。";
  if (value >= 70) return "开始留意会话长度，避免再开过大的问题分支。";
  return "当前上下文估算仍在舒适区。";
}

function widthLevel(percent) {
  return Math.max(0, Math.min(20, Math.round(Number(percent || 0) / 5)));
}

function visibleSessionName(session) {
  return privacyMask ? String(session.sessionId).slice(0, 8) : session.displayName;
}

function showNotice(message, isError = false) {
  const notice = $("#notice");
  notice.textContent = message;
  notice.classList.toggle("error", isError);
  notice.classList.remove("hidden");
  window.clearTimeout(showNotice.timer);
  showNotice.timer = window.setTimeout(() => notice.classList.add("hidden"), 3200);
}

function renderHealth() {
  const health = state.dashboard?.health;
  const element = $("#sidebar-status");
  if (!health) return;
  const healthy = health.status === "healthy";
  const scanning = health.status === "scanning";
  element.className = `sidebar-status ${healthy ? "healthy" : scanning ? "" : "degraded"}`;
  element.innerHTML = `<span class="status-dot"></span><div><strong>${healthy ? "采集正常" : scanning ? "正在建立索引" : "采集需注意"}</strong><small>${healthy ? `${health.watchedFiles} 个 rollout 文件` : escapeHtml(health.errorCode || "unknown")}</small></div>`;
  $("#last-update").textContent = health.lastScanAt ? `最后扫描 ${formatTime(health.lastScanAt)}` : "等待扫描";
}

function renderOverview() {
  const root = $("#overview-content");
  const data = state.dashboard;
  if (!data?.recent) {
    root.className = "empty-state";
    root.textContent = "还没有发现可统计的 Codex token 事件。监控器会继续只读扫描。";
    return;
  }

  const session = data.recent;
  const pressure = session.estimatedContextPercent;
  const pressureValue = pressure ?? 0;
  const latest = session.lastUsage;
  const today = state.usage?.today;
  const todayUsage = today?.usage || { totalTokens: 0, inputTokens: 0, outputTokens: 0 };
  const activeSessions = data.sessions.filter((item) => item.isActive).slice(0, 3);
  const recentSevenDays = (state.usage?.days || []).slice(-7).reduce((total, day) => total + Number(day.usage.totalTokens || 0), 0);
  const weeklyEstimate = state.usage?.costEstimate?.weekly;
  const selectedAnalysis = state.usageAnalysis;
  const annotationCoverage = selectedAnalysis?.selected.sessionCount > 0
    ? selectedAnalysis.annotatedSessionCount / selectedAnalysis.selected.sessionCount * 100
    : 0;
  root.className = "";
  root.innerHTML = `
    <div class="health-strip">
      <div class="health-copy"><span class="status-dot"></span><strong>${data.health.status === "healthy" ? "只读采集正常" : "采集状态需要检查"}</strong><span>${data.activeCount} 个活动会话</span></div>
      <div class="health-meta">最近成功 ${formatTime(data.health.lastSuccessfulScanAt)}</div>
    </div>
    <div class="overview-snapshot">
      <article class="context-panel ${pressureClass(pressureValue)}">
        <div class="context-panel-head"><div><span>当前上下文压力</span><h2>${pressure == null ? "暂无数据" : `${pressure}%`}</h2></div><span class="estimate-badge">估算值</span></div>
        <div class="context-progress" role="img" aria-label="当前上下文估算 ${pressure ?? 0}%"><i class="analysis-share-level-${widthLevel(pressureValue)}"></i></div>
        <div class="context-meta"><strong>${formatExact(latest.totalTokens)}</strong><span>/ ${formatExact(session.modelContextWindow)} token · 当前上下文估算 token / 模型窗口</span></div>
        <p>${pressureMessage(pressure)} 会话累计处理量包含历次调用，不等于当前上下文占用。</p>
      </article>
      <article class="overview-today-panel">
        <div class="overview-panel-label"><span>今日处理量</span><small>${today ? formatUsageDate(today.date) : "本地自然日"}</small></div>
        <strong class="overview-today-value">${formatTokens(todayUsage.totalTokens)}</strong>
        <p>${today?.sessionCount || 0} 个会话有增量</p>
        <div class="overview-today-split">
          <div><span>累计输入</span><strong>${formatTokens(todayUsage.inputTokens)}</strong></div>
          <div><span>累计输出</span><strong>${formatTokens(todayUsage.outputTokens)}</strong></div>
        </div>
      </article>
      <aside class="overview-live-panel" aria-label="活动会话摘要">
        <div class="overview-panel-label"><span>活动会话</span><small>当前活动窗口内</small></div>
        <div class="overview-live-count"><strong>${data.activeCount}</strong><span>个正在活动</span></div>
        <div class="overview-live-list">
          ${activeSessions.map((item) => `<div><i></i><span>${escapeHtml(visibleSessionName(item))}</span><small>${item.estimatedContextPercent ?? "--"}%</small></div>`).join("") || '<p>当前没有活动会话</p>'}
        </div>
      </aside>
    </div>
    <div class="metric-grid">
      ${metricCard("已索引会话", data.totalSessions, "本地只读统计")}
      ${metricCard("最近 7 天", recentSevenDays, "调用级事件合计")}
      ${metricCard("API 等值参考成本", weeklyEstimate?.estimatedCostUsd == null ? "--" : formatUsd(weeklyEstimate.estimatedCostUsd), "不是实际账单", "accent")}
      ${metricCard("已标记任务覆盖率", `${annotationCoverage.toFixed(0)}%`, "完成、返工与结果")}
    </div>
    <div class="subsection-head"><div><span class="section-number">02</span><h3>最近会话</h3></div><p>按最后活动时间排列 · 静止不代表已经结束</p></div>
    <div class="session-cards">${data.sessions.slice(0, 6).map(sessionCard).join("") || '<div class="empty-state">暂无会话</div>'}</div>`;
}

function metricCard(label, value, note, tone = "") {
  const displayValue = typeof value === "number" ? formatTokens(value) : escapeHtml(value);
  return `<article class="metric-card ${escapeHtml(tone)}"><span>${escapeHtml(label)}</span><strong>${displayValue}</strong><small>${escapeHtml(note)}</small></article>`;
}

function isPartiallyPriced(cost, usage) {
  if (!cost?.isPriced) return false;
  const totalTokens = Number(usage?.totalTokens ?? cost.usage?.totalTokens ?? 0);
  const pricedTokens = Number(cost.pricedTokens ?? 0);
  return pricedTokens > 0 && pricedTokens < totalTokens;
}

function costCoverageNote(cost, usage, completeLabel = "API 等值") {
  return isPartiallyPriced(cost, usage) ? "部分计价" : completeLabel;
}

function sessionCard(session) {
  const pressure = session.estimatedContextPercent ?? 0;
  const cost = session.cost?.isPriced ? formatUsd(session.cost.estimatedCostUsd) : "无法计价";
  const costNote = session.cost?.isPriced ? costCoverageNote(session.cost, session.totalUsage) : "无法计价";
  return `<article class="session-card"><div class="session-card-top"><span class="state-pill ${session.isActive ? "active" : ""}">${session.isActive ? "活动" : "静止"}</span><small>${formatTime(session.lastActivityAt)}</small></div><div class="session-name">${escapeHtml(visibleSessionName(session))}</div><strong>${formatTokens(session.totalUsage.totalTokens)}</strong><small>累计处理量 · 当前上下文 ${session.estimatedContextPercent ?? "--"}%</small><div class="session-cost-line"><span>${escapeHtml(session.model)}</span><b>${cost}</b></div><small class="session-cost-note">${costNote}</small><div class="mini-pressure ${pressureClass(pressure)}"><i class="analysis-share-level-${widthLevel(pressure)}"></i></div><button class="detail-link" type="button" data-action="details" data-session-id="${escapeHtml(session.sessionId)}">查看详情</button></article>`;
}

function renderSessions() {
  const query = $("#session-search").value.trim().toLowerCase();
  const filter = $("#session-filter").value;
  const rows = state.sessions.filter((session) => {
    const matchesQuery = !query || [session.displayName, session.sessionId, session.model].some((value) => String(value).toLowerCase().includes(query));
    const matchesFilter = filter === "all" ||
      (filter === "active" && session.isActive) ||
      (filter === "degraded" && session.dataStatus !== "healthy");
    return matchesQuery && matchesFilter;
  });
  if (state.sessionTokenSort) {
    const direction = state.sessionTokenSort === "desc" ? -1 : 1;
    rows.sort((left, right) =>
      (Number(left.totalUsage.totalTokens) - Number(right.totalUsage.totalTokens)) * direction);
  }
  const sortHeader = $("#session-token-sort-header");
  const sortButton = $("#session-token-sort");
  const sortIndicator = $("#session-token-sort-indicator");
  const descending = state.sessionTokenSort === "desc";
  sortHeader.setAttribute("aria-sort", descending ? "descending" : state.sessionTokenSort === "asc" ? "ascending" : "none");
  sortIndicator.textContent = descending ? "↓" : state.sessionTokenSort === "asc" ? "↑" : "↕";
  sortButton.setAttribute("aria-label", descending
    ? "当前按累计 token 从高到低排序，点击改为从低到高"
    : state.sessionTokenSort === "asc"
      ? "当前按累计 token 从低到高排序，点击改为从高到低"
      : "按累计 token 从高到低排序");
  $("#session-table-body").innerHTML = rows.map((session) => `
    <tr>
      <td class="session-cell"><strong>${escapeHtml(visibleSessionName(session))}</strong><small>${escapeHtml(privacyMask ? String(session.sessionId).slice(0, 8) : session.sessionId)} · <span class="state-pill ${session.isActive ? "active" : ""} ${session.dataStatus !== "healthy" ? "degraded" : ""}">${session.dataStatus !== "healthy" ? "异常" : session.isActive ? "活动" : "静止"}</span></small></td>
      <td>${escapeHtml(session.model)}<br><small>${escapeHtml(session.reasoningEffort)}</small></td>
      <td>${session.cost?.isPriced ? `<strong>${formatUsd(session.cost.estimatedCostUsd)}</strong><br><small>${costCoverageNote(session.cost, session.totalUsage)}</small>` : '<span class="cost-unpriced">无法计价</span>'}</td>
      <td><strong>${formatTokens(session.totalUsage.totalTokens)}</strong><br><small title="精确值">${formatExact(session.totalUsage.totalTokens)}</small></td>
      <td>${session.estimatedContextPercent ?? "--"}%<br><small>${formatExact(session.lastUsage.totalTokens)} / ${formatExact(session.modelContextWindow)}</small></td>
      <td>${formatTime(session.lastActivityAt)}</td>
      <td><button class="detail-button" type="button" data-action="details" data-session-id="${escapeHtml(session.sessionId)}">详情</button></td>
    </tr>`).join("") || '<tr><td colspan="7">没有符合条件的会话。</td></tr>';
}

function toggleSessionTokenSort() {
  state.sessionTokenSort = state.sessionTokenSort === "desc" ? "asc" : "desc";
  renderSessions();
}

function renderSessionDetail() {
  const session = state.sessions.find((item) => item.sessionId === state.selectedSessionId);
  const root = $("#session-detail-content");
  if (!session) {
    root.innerHTML = '<div class="detail-empty">该会话已不在当前列表中。</div>';
    return;
  }

  const pressure = session.estimatedContextPercent;
  const pressureValue = pressure ?? 0;
  const sessionId = privacyMask ? String(session.sessionId).slice(0, 8) : session.sessionId;
  const annotation = session.annotation || {
    taskType: "unclassified",
    outcome: "unmarked",
    humanMinutes: null,
    reworkReason: null,
    updatedAt: null
  };
  root.innerHTML = `
    <header class="detail-header">
      <h2 id="session-detail-title">${escapeHtml(visibleSessionName(session))}</h2>
      <p class="detail-session-id">${escapeHtml(sessionId)}</p>
      <div class="detail-tags">
        <span class="state-pill ${session.isActive ? "active" : ""} ${session.dataStatus !== "healthy" ? "degraded" : ""}">${session.dataStatus !== "healthy" ? "数据异常" : session.isActive ? "活动" : "静止"}</span>
        <span class="tag">${escapeHtml(session.model)}</span>
        <span class="tag">reasoning ${escapeHtml(session.reasoningEffort)}</span>
      </div>
    </header>
    <div class="detail-summary-grid">
      <article class="detail-total-card">
        <span>会话累计处理量</span>
        <strong>${formatTokens(session.totalUsage.totalTokens)}</strong>
        <small>精确值 ${formatExact(session.totalUsage.totalTokens)}</small>
      </article>
      <article class="detail-cost-card">
        <span>会话 API 等值参考成本</span>
        <strong>${session.cost?.isPriced ? formatUsd(session.cost.estimatedCostUsd) : "无法计价"}</strong>
        <p>${escapeHtml(session.model)}</p>
        <small>${session.cost?.isPriced ? `未缓存输入 ${formatTokens(session.cost.uncachedInputTokens)} · 缓存输入 ${formatTokens(session.totalUsage.cachedInputTokens)} · 输出 ${formatTokens(session.totalUsage.outputTokens)}${isPartiallyPriced(session.cost, session.totalUsage) ? ` · 未计价 ${formatTokens(Math.max(Number(session.totalUsage.totalTokens) - Number(session.cost.pricedTokens || 0), 0))}` : ""}` : "该模型没有可用参考单价"}</small>
      </article>
      <article class="detail-context-card">
        <div><span>当前上下文估算</span><strong>${pressure == null ? "--" : `${pressure}%`}</strong></div>
        <p>${formatExact(session.lastUsage.totalTokens)} / ${formatExact(session.modelContextWindow)} token</p>
        <div class="detail-pressure ${pressureClass(pressureValue)}"><i class="analysis-share-level-${widthLevel(pressureValue)}"></i></div>
        <small>输入 ${formatExact(session.lastUsage.inputTokens)} · 输出 ${formatExact(session.lastUsage.outputTokens)}</small>
      </article>
    </div>
    <section class="detail-section" aria-labelledby="detail-breakdown-title">
      <div class="detail-section-head"><h3 id="detail-breakdown-title">累计处理量细分</h3><span>细分项不重复计入总量</span></div>
      <div class="detail-metric-grid">
        ${detailMetric("累计输入", session.totalUsage.inputTokens)}
        ${detailMetric("累计输出", session.totalUsage.outputTokens)}
        ${detailMetric("缓存输入细分", session.totalUsage.cachedInputTokens)}
        ${detailMetric("推理输出细分", session.totalUsage.reasoningOutputTokens)}
      </div>
    </section>
    <section class="detail-section question-detail-section" aria-labelledby="question-detail-title">
      <div class="detail-section-head"><h3 id="question-detail-title">逐问题成本明细</h3><span>只读源文件 · 不写入数据库</span></div>
      ${renderQuestionDetails()}
    </section>
    <section class="detail-section annotation-section" aria-labelledby="detail-annotation-title">
      <div class="detail-section-head"><h3 id="detail-annotation-title">任务结果</h3><span>只写入监控器本地数据库</span></div>
      <form id="session-annotation-form" class="annotation-form">
        <label>任务类型<select name="taskType">${selectOptions(taskTypeLabels, annotation.taskType)}</select></label>
        <label>最终结果<select name="outcome">${selectOptions(outcomeLabels, annotation.outcome)}</select></label>
        <label>人工耗时，分钟<input name="humanMinutes" type="number" min="0" max="10080" value="${annotation.humanMinutes ?? ""}" placeholder="可不填"></label>
        <label>返工或问题原因<select name="reworkReason"><option value="">未记录</option>${selectOptions(reworkReasonLabels, annotation.reworkReason)}</select></label>
        <p>${annotation.updatedAt ? `上次保存 ${formatTime(annotation.updatedAt)}` : "尚未保存任务结果。问题预览仅在打开详情时只读获取，不写入数据库。"}</p>
        <button class="primary-button" type="submit">保存任务结果</button>
      </form>
    </section>
    <section class="detail-section" aria-labelledby="detail-status-title">
      <div class="detail-section-head"><h3 id="detail-status-title">会话状态</h3><span>只读采集</span></div>
      <dl class="detail-status-grid">
        ${detailStatus("模型", session.model)}
        ${detailStatus("Reasoning effort", session.reasoningEffort)}
        ${detailStatus("最后活动", formatTime(session.lastActivityAt))}
        ${detailStatus("最近成功采集", formatTime(session.lastSuccessAt))}
        ${detailStatus("采集状态", session.dataStatus === "healthy" ? "正常" : "异常")}
        ${detailStatus("模型上下文窗口", `${formatExact(session.modelContextWindow)} token`)}
      </dl>
      ${session.errorCode ? `<p class="detail-error">错误代码：${escapeHtml(session.errorCode)}</p>` : ""}
    </section>`;
}

function selectOptions(labels, selected) {
  return Object.entries(labels).map(([value, label]) =>
    `<option value="${escapeHtml(value)}" ${value === selected ? "selected" : ""}>${escapeHtml(label)}</option>`).join("");
}

function annotationLabel(labels, value) {
  return labels[value] || "未标记";
}

function detailMetric(label, value) {
  return `<article><span>${escapeHtml(label)}</span><strong>${formatTokens(value)}</strong><small>${formatExact(value)}</small></article>`;
}

function detailStatus(label, value) {
  return `<div><dt>${escapeHtml(label)}</dt><dd>${escapeHtml(value)}</dd></div>`;
}

function renderQuestionDetails() {
  if (state.sessionQuestionsLoading) return '<div class="question-loading">正在读取该会话的逐问题明细...</div>';
  if (state.sessionQuestionsError) return `<div class="question-empty">读取失败：${escapeHtml(state.sessionQuestionsError)}</div>`;
  const history = state.sessionQuestions;
  if (!history) return '<div class="question-loading">正在准备明细...</div>';
  if (!history.sourceAvailable) return '<div class="question-empty">原始 rollout 文件已不可用，无法还原逐问题明细。</div>';
  if (!history.questions.length) return '<div class="question-empty">这个会话还没有可展示的问题明细。</div>';
  return `<div class="question-history-meta"><span>${history.totalQuestions} 个问题</span><small>${history.isTruncated ? "仅显示最近 200 个" : "已显示全部"}</small></div>
    <div class="question-detail-list">${[...history.questions].reverse().map(questionDetailRow).join("")}</div>`;
}

function questionDetailRow(question) {
  const cost = question.isCostPriced ? formatUsd(question.estimatedCostUsd) : "无法计价";
  const prompt = privacyMask ? `第 ${question.sequence} 个问题（隐私模式已隐藏正文）` : question.promptPreview;
  return `<article class="question-detail-row">
    <div class="question-sequence">${String(question.sequence).padStart(2, "0")}</div>
    <div class="question-copy"><strong>${escapeHtml(prompt)}</strong><small>${formatTime(question.startedAt)} · ${question.modelCallCount} 次模型调用</small></div>
    <div class="question-model"><span>${escapeHtml(question.model)}</span><strong>${cost}</strong></div>
    <div class="question-token-grid">
      <span>输入 <b>${formatTokens(question.usage.inputTokens)}</b></span>
      <span>缓存 <b>${formatTokens(question.usage.cachedInputTokens)}</b></span>
      <span>输出 <b>${formatTokens(question.usage.outputTokens)}</b></span>
      <span>合计 <b>${formatTokens(question.usage.totalTokens)}</b></span>
    </div>
  </article>`;
}

function openSessionDetail(sessionId) {
  state.selectedSessionId = sessionId;
  state.sessionQuestions = null;
  state.sessionQuestionsLoading = true;
  state.sessionQuestionsError = "";
  state.annotationEditing = false;
  renderSessionDetail();
  const dialog = $("#session-detail-dialog");
  dialog.scrollTop = 0;
  if (!dialog.open) dialog.showModal();
  loadSessionQuestions(sessionId);
}

async function loadSessionQuestions(sessionId) {
  try {
    const history = await api(`/api/sessions/${encodeURIComponent(sessionId)}/questions?limit=200`);
    if (state.selectedSessionId !== sessionId) return;
    state.sessionQuestions = history;
    state.sessionQuestionsError = "";
  } catch (error) {
    if (state.selectedSessionId !== sessionId) return;
    state.sessionQuestionsError = error.message;
  } finally {
    if (state.selectedSessionId === sessionId) {
      state.sessionQuestionsLoading = false;
      renderSessionDetail();
    }
  }
}

function renderModels() {
  const maximum = Math.max(...state.models.map((model) => model.totalTokens), 1);
  $("#model-grid").innerHTML = state.models.map((model) => {
    const input = model.inputTokens || 0;
    const output = model.outputTokens || 0;
    const cached = model.cachedInputTokens || 0;
    const denominator = Math.max(input + output, 1);
    const inputShare = input / denominator * 100;
    const inputLevel = widthLevel(inputShare);
    const outputLevel = 20 - inputLevel;
    const scale = model.totalTokens / maximum * 100;
    return `<article class="model-card">
      <div class="model-head"><div><h3>${escapeHtml(model.model)}</h3><p>${model.sessionCount} 个会话 · 占最高模型 ${scale.toFixed(0)}%</p></div><div class="model-total"><strong>${formatTokens(model.totalTokens)}</strong><small>累计 token</small></div></div>
      <div class="share-track" title="输入与输出构成" role="img" aria-label="输入 ${inputShare.toFixed(1)}%，输出 ${(100 - inputShare).toFixed(1)}%"><i class="analysis-share-level-${inputLevel}"></i><i class="analysis-share-level-${outputLevel}"></i></div>
      <div class="model-stats"><div><span>会话中位数</span><strong>${formatTokens(model.medianTokens)}</strong></div><div><span>缓存输入细分</span><strong>${formatTokens(cached)}</strong></div><div><span>推理输出细分</span><strong>${formatTokens(model.reasoningOutputTokens)}</strong></div></div>
      <details><summary>展开 reasoning effort</summary>${model.reasoning.map((item) => `<div class="reasoning-row"><span>${escapeHtml(item.reasoningEffort)}</span><span>${item.sessionCount} 会话</span><strong>${formatTokens(item.totalTokens)}</strong></div>`).join("")}</details>
    </article>`;
  }).join("") || '<div class="empty-state">还没有足够的数据生成模型对比。</div>';
}

function renderUsage() {
  const root = $("#usage-content");
  const data = state.usage;
  if (!data) return;
  const analysis = state.usageAnalysis;
  const selectedDate = analysis?.selected.date || data.today.date;
  const maximum = Math.max(...data.days.map((day) => day.usage.totalTokens), 1);
  const recentRows = [...data.days].slice(-7).reverse();
  root.className = "";
  root.innerHTML = `
    <div class="usage-summary-grid">
      ${usageSummaryCard("today", "今日处理量", data.today.usage, `${data.today.sessionCount} 个有增量的会话`)}
      ${usageSummaryCard("all", "累计处理量", data.allTime, `${data.totalSessions} 个已统计会话`)}
    </div>
    <div class="usage-method-note">
      <strong>统计口径</strong>
      <span>总处理量累加每次调用的报告值。输入、输出、缓存输入和推理输出用于拆分查看。</span>
      <small>${escapeHtml(data.timeZoneId)} · 每 3 秒更新</small>
    </div>
    ${usageCostPanel(data.costEstimate)}
    ${analysis ? usageAnalysisPanel(analysis, data) : '<div class="loading-panel usage-analysis-loading">正在分析所选日期...</div>'}
    <article class="usage-chart-card">
      <div class="usage-card-head"><div><h3>最近 14 天</h3></div><span>每日总处理量</span></div>
      <div class="usage-chart-scroll">
        <div class="usage-chart" aria-label="最近 14 天 token 总处理量柱状图">
          ${data.days.map((day) => usageBar(day, maximum, selectedDate)).join("")}
        </div>
      </div>
    </article>
    <article class="daily-ledger-card">
      <div class="usage-card-head"><div><h3>最近 7 天明细</h3></div><span>缓存输入不重复相加</span></div>
      <div class="table-wrap daily-table-wrap">
        <table class="daily-table">
          <thead><tr><th>日期</th><th>总处理量</th><th>输入</th><th>输出</th><th>缓存输入细分</th><th>会话</th></tr></thead>
          <tbody>${recentRows.map((day) => `<tr class="${day.date === selectedDate ? "selected" : ""}"><td><button class="usage-date-button" type="button" data-analysis-date="${escapeHtml(day.date)}">${formatUsageDate(day.date)}</button></td><td><strong>${formatTokens(day.usage.totalTokens)}</strong><br><small>${formatExact(day.usage.totalTokens)}</small></td><td>${formatTokens(day.usage.inputTokens)}</td><td>${formatTokens(day.usage.outputTokens)}</td><td>${formatTokens(day.usage.cachedInputTokens)}</td><td>${day.sessionCount}</td></tr>`).join("")}</tbody>
        </table>
      </div>
    </article>`;
}

function usageAnalysisPanel(analysis, data) {
  const selected = analysis.selected;
  const previousTotal = analysis.previousDay.usage.totalTokens;
  const average = analysis.sevenDayAverageTokens;
  const coverage = selected.sessionCount > 0 ? analysis.annotatedSessionCount / selected.sessionCount * 100 : 0;
  const completedAverage = analysis.completedSessionCount > 0 ? analysis.completedTokens / analysis.completedSessionCount : 0;
  const reworkShare = selected.usage.totalTokens > 0 ? analysis.reworkTokens / selected.usage.totalTokens * 100 : 0;
  const dateOptions = [...data.days].reverse().map((day) =>
    `<option value="${escapeHtml(day.date)}" ${day.date === selected.date ? "selected" : ""}>${formatUsageDate(day.date)}</option>`).join("");
  return `<article class="usage-analysis-card">
    <div class="analysis-toolbar">
      <div><h3>这一天为什么消耗这些 token</h3></div>
      <label for="usage-analysis-date">分析日期<select id="usage-analysis-date">${dateOptions}</select></label>
    </div>
    <div class="analysis-kpi-grid">
      <article class="analysis-kpi"><span>所选日期</span><strong>${formatTokens(selected.usage.totalTokens)}</strong><small>${formatUsageDate(selected.date)} · 精确值 ${formatExact(selected.usage.totalTokens)}</small></article>
      ${analysisComparisonCard("相比前一天", selected.usage.totalTokens, previousTotal, formatUsageDate(analysis.previousDay.date))}
      ${analysisComparisonCard("相比前 7 日平均", selected.usage.totalTokens, average, `日均 ${formatTokens(average)}`)}
      <article class="analysis-kpi"><span>有增量的会话</span><strong>${selected.sessionCount}</strong><small>按所选日期去重</small></article>
    </div>
    <p class="analysis-insight">${escapeHtml(usageInsight(analysis))}</p>
    <div class="analysis-result-head"><div><h4>任务结果分析</h4><p>结果取自当前手动标记，并应用到该会话出现过的日期</p></div><span>${analysis.annotatedSessionCount} / ${selected.sessionCount} 已标记</span></div>
    <div class="analysis-result-grid">
      <article><span>已标记覆盖率</span><strong>${coverage.toFixed(0)}%</strong><small>${analysis.annotatedSessionCount} 个会话有最终结果</small></article>
      <article><span>已完成任务平均 token</span><strong>${analysis.completedSessionCount > 0 ? formatTokens(completedAverage) : "--"}</strong><small>${analysis.completedSessionCount} 个已完成会话</small></article>
      <article><span>返工 token 占比</span><strong>${reworkShare.toFixed(1)}%</strong><small>${analysis.reworkSessionCount} 个返工会话 · ${formatTokens(analysis.reworkTokens)}</small></article>
    </div>
    <div class="analysis-contribution-head"><div><h4>当日 Top 5 会话</h4><p>按对所选日期总处理量的贡献排序</p></div><span>${analysis.topSessions.length} 个结果</span></div>
    <div class="analysis-contribution-list">
      ${analysis.topSessions.map((session, index) => usageContributionRow(session, index, selected.usage.totalTokens)).join("") || '<div class="analysis-empty">这一天没有可分析的 token 增量。</div>'}
    </div>
  </article>`;
}

function analysisComparisonCard(label, current, baseline, baselineNote) {
  const difference = current - baseline;
  const tone = difference > 0 ? "up" : difference < 0 ? "down" : "flat";
  const change = baseline > 0
    ? `${difference >= 0 ? "+" : ""}${(difference / baseline * 100).toFixed(0)}%`
    : current > 0 ? "新增" : "0%";
  const detail = baseline > 0
    ? `${baselineNote} · ${difference >= 0 ? "+" : ""}${formatTokens(difference)}`
    : `${baselineNote} · 基线为 0`;
  return `<article class="analysis-kpi ${tone}"><span>${escapeHtml(label)}</span><strong>${escapeHtml(change)}</strong><small>${escapeHtml(detail)}</small></article>`;
}

function usageInsight(analysis) {
  const selected = analysis.selected.usage;
  const average = analysis.sevenDayAverageTokens;
  const top = analysis.topSessions[0];
  const parts = [];
  if (average > 0) {
    const percent = Math.abs((selected.totalTokens - average) / average * 100).toFixed(0);
    parts.push(`当日比此前 7 日平均${selected.totalTokens >= average ? "高" : "低"} ${percent}%。`);
  } else {
    parts.push("此前 7 日没有可用消耗基线，暂时不能判断趋势。");
  }
  if (top && selected.totalTokens > 0) {
    const share = top.usage.totalTokens / selected.totalTokens * 100;
    parts.push(`消耗最高的会话贡献 ${share.toFixed(0)}%${share >= 50 ? "，是当日主要来源" : ""}。`);
  }
  const inputAndOutput = selected.inputTokens + selected.outputTokens;
  if (inputAndOutput > 0) {
    parts.push(`输入占输入与输出合计的 ${(selected.inputTokens / inputAndOutput * 100).toFixed(0)}%，缓存输入细分为 ${formatTokens(selected.cachedInputTokens)}。`);
  }
  return parts.join("");
}

function usageContributionRow(session, index, dailyTotal) {
  const share = dailyTotal > 0 ? session.usage.totalTokens / dailyTotal * 100 : 0;
  const level = share === 0 ? 0 : Math.max(1, Math.ceil(share / 5));
  const displayName = privacyMask ? String(session.sessionId).slice(0, 8) : session.displayName;
  const annotation = session.annotation || { taskType: "unclassified", outcome: "unmarked" };
  return `<article class="analysis-contribution-row">
    <span class="analysis-rank">${String(index + 1).padStart(2, "0")}</span>
    <div class="analysis-session-copy"><strong>${escapeHtml(displayName)}</strong><small>${escapeHtml(annotationLabel(taskTypeLabels, annotation.taskType))} · ${escapeHtml(annotationLabel(outcomeLabels, annotation.outcome))} · ${escapeHtml(session.model)} · 最后增量 ${formatTime(session.lastEventAt)}</small></div>
    <div class="analysis-session-total"><strong>${formatTokens(session.usage.totalTokens)}</strong><small>${share.toFixed(1)}%</small></div>
    <div class="analysis-share-track" role="img" aria-label="占当日处理量 ${share.toFixed(1)}%"><i class="analysis-share-level-${level}"></i></div>
    <button class="detail-button" type="button" data-action="details" data-session-id="${escapeHtml(session.sessionId)}">详情</button>
  </article>`;
}

function usageSummaryCard(kind, label, usage, note) {
  return `<article class="usage-summary-card ${kind}"><span class="usage-card-icon" aria-hidden="true"><i></i><i></i><i></i></span><div><span class="usage-card-label">${escapeHtml(label)}</span><strong>${formatTokens(usage.totalTokens)}</strong><p>输入 ${formatTokens(usage.inputTokens)} · 输出 ${formatTokens(usage.outputTokens)} · 缓存细分 ${formatTokens(usage.cachedInputTokens)}</p><small>精确值 ${formatExact(usage.totalTokens)} · ${escapeHtml(note)}</small></div></article>`;
}

function usageCostPanel(estimate) {
  if (!estimate) return "";
  const pricing = estimate.pricingStatus;
  const periods = [
    ["每日", "日 token", estimate.daily],
    ["每周", "周 token", estimate.weekly],
    ["每月", "月 token", estimate.monthly]
  ];
  return `<article class="usage-estimate-card">
    <div class="usage-estimate-head">
      <div><h3>Token 目录价估算</h3><p>每条用量按发生时间匹配当时生效的价格，优先官方，再使用第三方和内置参考价。</p></div>
      <span class="estimate-badge ${pricing?.isStale ? "stale" : ""}">${pricing?.isStale ? "价格已过期" : "价格可用"}</span>
    </div>
    ${pricingStatusPanel(pricing)}
    <div class="usage-estimate-grid">
      ${periods.map(([label, tokenLabel, period]) => usageCostPeriod(label, tokenLabel, period)).join("")}
    </div>
    <details class="cost-rate-details">
      <summary>查看当前价格与来源</summary>
      <div>${estimate.rates.map((rate) => `<span><b>${escapeHtml(rate.model)}</b> 输入 ${formatUsd(rate.inputUsdPerMillion)} · 缓存 ${formatUsd(rate.cachedInputUsdPerMillion)} · 输出 ${formatUsd(rate.outputUsdPerMillion)} / 百万 token<br>${escapeHtml(priceSourceLabel(rate.sourceKind))} · ${rate.effectiveFrom ? `生效于 ${formatTime(rate.effectiveFrom)}` : "无远程生效时间"}</span>`).join("")}</div>
    </details>
    <p class="usage-estimate-note">金额是美元目录价估算，不是 Codex Desktop、ChatGPT 订阅或账户实际扣款。官方来源未验证时，官方覆盖金额为 0，总估算仍可使用明确标记的参考价。当前价格日期：${formatUsageDate(estimate.pricingAsOf)}。</p>
  </article>`;
}

function pricingStatusPanel(pricing) {
  if (!pricing) return '<div class="pricing-status-strip gated"><strong>价格状态不可用</strong><span>当前仅能使用内置参考价</span></div>';
  const officialReady = pricing.sources?.some((source) => source.sourceKind === "official" && source.enabled && source.lastSuccessAt);
  const thirdPartyReady = pricing.sources?.some((source) => source.sourceKind === "third_party_reference" && source.enabled && source.lastSuccessAt);
  const sourceState = officialReady ? "官方价格已连接" : thirdPartyReady ? "第三方参考价已连接" : "远程价格尚未启用";
  const update = pricing.lastSuccessfulUpdateAt ? `最后成功 ${formatTime(pricing.lastSuccessfulUpdateAt)}` : "尚无远程成功记录";
  const quarantine = pricing.quarantinedChanges?.length
    ? ` · ${pricing.quarantinedChanges.length} 项异常变化待复核`
    : "";
  return `<div class="pricing-status-strip ${pricing.isStale ? "stale" : "ready"}"><strong>${escapeHtml(sourceState)}</strong><span>${escapeHtml(update + quarantine)}</span><small>官方 Gate：${escapeHtml(gateLabel(pricing.officialGate))}</small></div>`;
}

function priceSourceLabel(kind) {
  if (kind === "official") return "OpenAI 官方价格";
  if (kind === "third_party_reference") return "第三方参考价";
  return "内置默认参考价";
}

function gateLabel(value) {
  const labels = {
    disabled_pending_official_source_verification: "待核验真实官方来源",
    verified_official_markdown_table_v1: "官方 Markdown 价格表已接通",
    disabled_by_operator: "由操作者关闭",
    disabled_pending_live_schema_and_license_verification: "待核验实时格式与许可证",
    operator_override_unverified: "操作者强制启用，尚未完成官方核验"
  };
  return labels[value] || value || "未说明";
}

function usageCostPeriod(label, tokenLabel, period) {
  const total = Number(period.usage.totalTokens || 0);
  const priced = Number(period.pricedTokens || 0);
  const coverage = total > 0 ? Math.min(priced / total, 1) : 1;
  const level = priced === 0 ? 0 : Math.max(1, Math.ceil(coverage * 20));
  const unpricedNote = period.unpricedTokens > 0
    ? `${formatTokens(period.unpricedTokens)} 未计价`
    : "全部已计价";
  const officialCoverage = total > 0 ? Math.min(Number(period.officialPricedTokens || 0) / total, 1) : 0;
  const coverageRows = (period.coverage || []).map((item) =>
    `<div class="usage-estimate-projection"><span>${escapeHtml(priceSourceLabel(item.sourceKind))}</span><b>${formatTokens(item.tokens)} · ${formatUsd(item.estimatedCostUsd)}</b></div>`).join("");
  return `<section class="usage-estimate-period">
    <div class="usage-estimate-period-head"><span>${escapeHtml(label)}</span><small>${escapeHtml(unpricedNote)}</small></div>
    <strong>${formatUsd(period.estimatedCostUsd)}</strong>
    <p>API 等值参考成本 · 美元</p>
    <div class="usage-estimate-track" role="img" aria-label="已计价 token 覆盖 ${(coverage * 100).toFixed(1)}%"><i class="analysis-share-level-${level}"></i></div>
    <div class="usage-estimate-projection"><span>${escapeHtml(tokenLabel)}</span><b>${formatTokens(total)}</b></div>
    <div class="usage-estimate-projection"><span>计价覆盖</span><b>${(coverage * 100).toFixed(1)}%</b></div>
    <div class="usage-estimate-projection"><span>官方覆盖</span><b>${(officialCoverage * 100).toFixed(1)}% · ${formatUsd(period.officialEstimatedCostUsd || 0)}</b></div>
    <div class="usage-estimate-coverage">${coverageRows || "<small>尚无可用价格来源</small>"}</div>
    <small class="usage-estimate-range">${formatUsageDate(period.startDate)} — ${formatUsageDate(period.endDate)}</small>
  </section>`;
}

function usageBar(day, maximum, selectedDate) {
  const total = day.usage.totalTokens;
  const level = total === 0 ? 0 : Math.max(1, Math.ceil(total / maximum * 20));
  return `<button class="usage-day ${day.date === selectedDate ? "selected" : ""}" type="button" data-analysis-date="${escapeHtml(day.date)}" title="${escapeHtml(day.date)} · ${formatExact(total)} token"><span>${total === 0 ? "0" : formatTokens(total)}</span><div class="usage-bar-track"><i class="usage-bar-level-${level}"></i></div><small>${formatUsageDate(day.date, true)}</small></button>`;
}

function formatUsageDate(value, short = false) {
  const [year, month, day] = String(value).split("-");
  return short ? `${month}/${day}` : `${year}/${month}/${day}`;
}

function renderRankings() {
  const root = $("#ranking-content");
  const data = state.rankings;
  if (!data) return;

  const mode = state.rankingMode;
  $$(".ranking-mode").forEach((button) => {
    const active = button.dataset.rankingMode === state.rankingMode;
    button.classList.toggle("active", active);
    button.setAttribute("aria-pressed", String(active));
  });
  root.className = "";

  if (mode === "projects") {
    renderProjectRankings(root, data);
    return;
  }

  const items = mode === "days" ? data.days : data.sessions;
  const rankingLabel = mode === "days" ? "日期榜" : "会话榜";
  if (!items.length) {
    root.innerHTML = '<div class="empty-state">还没有可排名的 token 消耗记录。</div>';
    return;
  }

  root.innerHTML = `
    <div class="ranking-meta">
      <div><strong>${rankingLabel}</strong><span>${items.length} 条有效记录</span></div>
      <small>${escapeHtml(data.timeZoneId)} · 总 token 降序</small>
    </div>
    <div class="ranking-podium">${items.slice(0, 3).map((item, index) => rankingPodiumCard(item, index, mode)).join("")}</div>
    <div class="table-wrap ranking-table-wrap">
      ${mode === "days" ? dayRankingTable(items) : sessionRankingTable(items)}
    </div>`;
}

function renderProjectRankings(root, rankings) {
  const daily = state.dailyProjects;
  const dailyRange = state.projectRankingRange === "daily";
  const rangeToolbar = `
    <div class="project-range-toolbar" role="group" aria-label="项目统计范围">
      <button type="button" data-project-range="daily" class="${dailyRange ? "active" : ""}" aria-pressed="${dailyRange}">所选日期</button>
      <button type="button" data-project-range="all" class="${dailyRange ? "" : "active"}" aria-pressed="${!dailyRange}">历史累计</button>
    </div>`;

  if (!dailyRange) {
    const items = rankings.projects;
    root.innerHTML = `${rangeToolbar}
      ${items.length ? `<div class="ranking-meta">
        <div><strong>项目累计榜</strong><span>${items.length} 条有效记录</span></div>
        <small>${escapeHtml(rankings.timeZoneId)} · 会话累计 token 降序</small>
      </div>
      <div class="ranking-podium">${items.slice(0, 3).map((item, index) => rankingPodiumCard(item, index, "projects")).join("")}</div>
      <div class="table-wrap ranking-table-wrap">${projectRankingTable(items)}</div>` : '<div class="empty-state">还没有可排名的项目累计记录。</div>'}`;
    return;
  }

  if (!daily) {
    root.innerHTML = `${rangeToolbar}<div class="loading-panel">正在读取每日项目明细...</div>`;
    return;
  }

  prepareProjectExpansion(daily);
  const earliest = daily.earliestDate || daily.latestDate;
  const previousDisabled = !daily.earliestDate || daily.date <= earliest;
  const nextDisabled = daily.date >= daily.latestDate;
  const projects = daily.projects || [];
  root.innerHTML = `${rangeToolbar}
    <div class="project-date-toolbar">
      <div class="project-date-nav" role="group" aria-label="项目明细日期">
        <button type="button" data-project-date-action="previous" ${previousDisabled ? "disabled" : ""}>前一天</button>
        <label>日期<input id="project-ranking-date" type="date" value="${escapeHtml(daily.date)}" min="${escapeHtml(earliest)}" max="${escapeHtml(daily.latestDate)}"></label>
        <button type="button" data-project-date-action="next" ${nextDisabled ? "disabled" : ""}>后一天</button>
        <button type="button" data-project-date-action="today" ${nextDisabled ? "disabled" : ""}>回到今天</button>
      </div>
      <div class="project-expand-actions" role="group" aria-label="项目展开状态">
        <button type="button" data-project-expand-all="true" ${projects.length ? "" : "disabled"}>全部展开</button>
        <button type="button" data-project-expand-all="false" ${projects.length ? "" : "disabled"}>全部收起</button>
      </div>
    </div>
    <div class="daily-project-summary">
      <article><span>所选日期</span><strong>${formatUsageDate(daily.date)}</strong><small>${escapeHtml(daily.timeZoneId)}</small></article>
      <article><span>当日总 token</span><strong>${formatTokens(daily.usage.totalTokens)}</strong><small>精确值 ${formatExact(daily.usage.totalTokens)}</small></article>
      <article><span>正消耗项目</span><strong>${formatExact(projects.length)}</strong><small>${formatExact(daily.sessionCount)} 个会话</small></article>
    </div>
    ${projects.length
      ? `<div class="daily-project-list">${projects.map((project, index) => dailyProjectGroup(project, index, daily.usage.totalTokens)).join("")}</div>`
      : '<div class="empty-state project-empty-state">这一天没有 token 增量。日期仍保留在完整时间线上。</div>'}`;
}

function prepareProjectExpansion(data) {
  if (state.projectExpansionDate === data.date) return;
  state.projectExpansionDate = data.date;
  state.expandedProjectKeys = new Set();
  if (data.projects?.length) state.expandedProjectKeys.add(projectIdentityKey(data.projects[0]));
}

function projectIdentityKey(project) {
  const path = String(project.projectPath || "").toLowerCase();
  return `${project.projectStatus || "unidentified"}:${path}`;
}

function dailyProjectGroup(project, index, dailyTotal) {
  const key = projectIdentityKey(project);
  const expanded = state.expandedProjectKeys.has(key);
  const share = dailyTotal > 0 ? project.usage.totalTokens / dailyTotal * 100 : 0;
  const displayName = privacyMask ? `项目 ${String(index + 1).padStart(2, "0")}` : project.displayName;
  const path = privacyMask ? "项目路径已隐藏" : project.projectPath || "未记录工作目录";
  const status = projectStatusLabel(project.projectStatus);
  return `<article class="daily-project-group ${expanded ? "expanded" : ""}">
    <button class="daily-project-head" type="button" data-project-key="${escapeHtml(key)}" aria-expanded="${expanded}">
      <span class="daily-project-chevron" aria-hidden="true">${expanded ? "−" : "+"}</span>
      <span class="daily-project-copy"><strong>${escapeHtml(displayName)}</strong><small>${escapeHtml(path)}</small></span>
      <span class="project-status ${escapeHtml(project.projectStatus)}">${escapeHtml(status)}</span>
      <span class="daily-project-stat"><strong>${formatTokens(project.usage.totalTokens)}</strong><small>${share.toFixed(1)}% · ${project.sessions.length} 个会话</small></span>
    </button>
    <div class="daily-project-body" ${expanded ? "" : "hidden"}>
      <div class="table-wrap daily-project-table-wrap">${dailyProjectSessionTable(project)}</div>
    </div>
  </article>`;
}

function dailyProjectSessionTable(project) {
  return `<table class="ranking-table daily-project-session-table">
    <thead><tr><th>会话</th><th>当日总 token</th><th>输入</th><th>输出</th><th>缓存输入细分</th><th>Reasoning 细分</th><th>项目占比</th><th>模型</th><th>最后增量</th><th>操作</th></tr></thead>
    <tbody>${project.sessions.map((session) => {
      const share = project.usage.totalTokens > 0 ? session.usage.totalTokens / project.usage.totalTokens * 100 : 0;
      return `<tr>
        <td class="session-cell"><strong>${escapeHtml(visibleRankingSessionName(session))}</strong><small>${escapeHtml(privacyMask ? String(session.sessionId).slice(0, 8) : session.sessionId)}</small></td>
        <td><strong>${formatTokens(session.usage.totalTokens)}</strong><br><small>${formatExact(session.usage.totalTokens)}</small></td>
        <td>${formatTokens(session.usage.inputTokens)}</td>
        <td>${formatTokens(session.usage.outputTokens)}</td>
        <td>${formatTokens(session.usage.cachedInputTokens)}</td>
        <td>${formatTokens(session.usage.reasoningOutputTokens)}</td>
        <td>${share.toFixed(1)}%</td>
        <td>${escapeHtml(session.model)}<br><small>${escapeHtml(session.reasoningEffort)}</small></td>
        <td>${formatTime(session.lastEventAt)}</td>
        <td><button class="detail-button" type="button" data-action="details" data-session-id="${escapeHtml(session.sessionId)}">详情</button></td>
      </tr>`;
    }).join("")}</tbody>
  </table>`;
}

function projectStatusLabel(status) {
  if (status === "git") return "Git 项目";
  if (status === "directory") return "工作目录";
  if (status === "pending") return "待归并";
  return "未识别";
}

function addIsoDays(value, days) {
  const [year, month, day] = String(value).split("-").map(Number);
  const date = new Date(Date.UTC(year, month - 1, day + days));
  return date.toISOString().slice(0, 10);
}

async function selectProjectRankingDate(date) {
  if (!date || date === state.selectedProjectDate) return;
  try {
    const dailyProjects = await api(`/api/rankings/projects/daily?date=${encodeURIComponent(date)}`);
    state.selectedProjectDate = date;
    state.dailyProjects = dailyProjects;
    state.projectExpansionDate = null;
    renderRankings();
  } catch (error) {
    showNotice(`无法读取项目日明细：${error.message}`, true);
  }
}

function renderSkills() {
  const root = $("#skills-content");
  const data = state.skills;
  if (!data) return;
  const status = $("#skill-status").value;
  const skills = data.skills.filter((skill) => status === "all" || (status === "active") === skill.isActive);
  root.className = "";
  root.innerHTML = `
    <div class="skill-summary-grid">
      ${skillMetric("当前可用", data.activeSkillCount, "去重后的 skill 定义")}
      ${skillMetric("识别调用", data.invocationCount, "按当前置信度筛选")}
      ${skillMetric("独占 token", formatTokens(data.exclusiveTokens), "只激活一个 skill")}
      ${skillMetric("协同 token", formatTokens(data.collaborativeTokens), "组合层只计算一次")}
      ${skillMetric("未归因", formatTokens(data.unattributedTokens), `覆盖率 ${Number(data.coveragePercent).toFixed(1)}%`)}
    </div>
    <div class="skill-method-note"><strong>归因规则</strong><span>成功读取完整 SKILL.md 才是高置信度；相同累计 usage 快照先去重；缓存输入和 reasoning 只是 total 的细分。</span></div>
    <div class="table-wrap skill-table-wrap">
      <table class="ranking-table skill-table">
        <thead><tr><th>Skill</th><th>基础体积</th><th>平均加载</th><th>调用</th><th>独占 token</th><th>涉及协同</th><th>参考成本</th><th>最后使用</th><th>操作</th></tr></thead>
        <tbody>${skills.map((skill) => `<tr>
          <td class="session-cell"><strong>${escapeHtml(skill.name)}</strong><small>${escapeHtml(skill.sourceKind)} · ${skill.isActive ? "当前可用" : "非活动"}</small></td>
          <td><strong>${formatTokens(skill.baseTokens)}</strong><br><small>${escapeHtml(skill.tokenizer)}${skill.isEstimated ? " · 估算" : ""}</small></td>
          <td>${formatTokens(skill.averageLoadedTokens)}</td>
          <td>${formatExact(skill.invocationCount)}</td>
          <td><strong>${formatTokens(skill.exclusiveUsage.totalTokens)}</strong><br><small>输入 ${formatTokens(skill.exclusiveUsage.inputTokens)} · 输出 ${formatTokens(skill.exclusiveUsage.outputTokens)}</small></td>
          <td>${formatTokens(skill.collaborativeUsage.totalTokens)}</td>
          <td>${formatUsd(skill.estimatedCostUsd)}${skill.unpricedTokens > 0 ? `<br><small>${formatTokens(skill.unpricedTokens)} 未计价</small>` : ""}</td>
          <td>${skill.lastUsedAt ? formatTime(skill.lastUsedAt) : "尚未识别"}</td>
          <td><button class="detail-button" type="button" data-skill-name="${escapeHtml(skill.name)}">详情</button></td>
        </tr>`).join("") || '<tr><td colspan="9">当前筛选下没有 skill。</td></tr>'}</tbody>
      </table>
    </div>
    ${data.collaborations.length ? `<div class="skill-collaboration"><div class="subsection-head"><div><h3>协同组合</h3></div><p>组合 token 不重复拆给成员</p></div>${data.collaborations.slice(0, 8).map((item) => `<article><strong>${escapeHtml(item.key)}</strong><span>${item.modelCallCount} 次模型调用 · ${formatUsd(item.estimatedCostUsd)}${item.unpricedTokens > 0 ? ` · ${formatTokens(item.unpricedTokens)} 未计价` : ""}</span><b>${formatTokens(item.usage.totalTokens)}</b></article>`).join("")}</div>` : ""}`;
}

function skillMetric(label, value, note) {
  return `<article><span>${escapeHtml(label)}</span><strong>${escapeHtml(value)}</strong><small>${escapeHtml(note)}</small></article>`;
}

async function openSkillDetail(name) {
  const panel = $("#skill-detail");
  panel.hidden = false;
  panel.innerHTML = '<div class="loading-panel">正在读取版本与调用证据...</div>';
  try {
    state.skillDetail = await api(`/api/skills/${encodeURIComponent(name)}`);
    renderSkillDetail();
    panel.scrollIntoView({ behavior: "smooth", block: "start" });
  } catch (error) {
    panel.innerHTML = `<div class="empty-state">无法读取 skill 详情：${escapeHtml(error.message)}</div>`;
  }
}

function renderSkillDetail() {
  const panel = $("#skill-detail");
  const detail = state.skillDetail;
  if (!detail) return;
  const skill = detail.skill;
  panel.innerHTML = `
    <div class="skill-detail-head"><div><span>归因证据</span><h3>${escapeHtml(skill.name)}</h3><p>运行用量是 skill 生效窗口的处理量，不是反事实节省量。</p></div><button id="skill-detail-close" class="detail-button" type="button">收起</button></div>
    <div class="skill-version-grid">${skill.versions.map((version) => `<article><span>${escapeHtml(version.contentHash.slice(0, 12))}</span><strong>${formatTokens(version.baseTokens)} token</strong><small>${escapeHtml(privacyMask ? "路径已隐藏" : version.path)}<br>${escapeHtml(version.tokenizer)} ${escapeHtml(version.tokenizerVersion)}${version.isEstimated ? " · 估算" : ""}</small></article>`).join("")}</div>
    <div class="skill-detail-columns">
      <section><h4>最近调用</h4>${detail.invocations.slice(0, 30).map((item) => `<div class="skill-timeline-row"><i class="${item.confidence}"></i><div><strong>${formatTime(item.startedAt)}</strong><small>${escapeHtml(item.model)} · ${escapeHtml(item.confidence)} · 加载 ${formatTokens(item.loadedTokens)}</small></div></div>`).join("") || '<p class="detail-empty">没有调用记录。</p>'}</section>
      <section><h4>最近归因窗口</h4>${detail.attributionEvents.slice(0, 30).map((item) => `<div class="skill-attribution-row"><div><strong>${escapeHtml(item.bucketType === "exclusive" ? "独占" : "协同")}</strong><small>${escapeHtml(item.bucketKey)} · ${escapeHtml(item.model)}</small></div><b>${formatTokens(item.usage.totalTokens)}</b></div>`).join("") || '<p class="detail-empty">没有归因窗口。</p>'}</section>
    </div>`;
  $("#skill-detail-close").addEventListener("click", () => { panel.hidden = true; state.skillDetail = null; });
}

async function refreshSkills() {
  const days = $("#skill-days").value;
  const confidence = $("#skill-confidence").value;
  const model = $("#skill-model").value.trim();
  state.skills = await api(`/api/skills?days=${encodeURIComponent(days)}&confidence=${encodeURIComponent(confidence)}&model=${encodeURIComponent(model)}`);
  renderSkills();
}

function rankingPodiumCard(item, index, mode) {
  const rank = index + 1;
  const label = mode === "days"
    ? formatUsageDate(item.date)
    : mode === "projects"
      ? visibleProjectName(item, index)
      : visibleRankingSessionName(item);
  const note = mode === "days"
    ? `${item.sessionCount} 个会话`
    : mode === "projects"
      ? `${item.sessionCount} 个会话 · ${item.modelCount} 个模型`
      : `${item.model} · ${formatTime(item.lastActivityAt)}`;
  const detail = mode === "projects"
    ? `${formatProjectCost(item)} · 精确值 ${formatExact(item.usage.totalTokens)}`
    : `精确值 ${formatExact(item.usage.totalTokens)}`;
  return `<article class="ranking-podium-card rank-${rank}">
    <span class="ranking-medal">${String(rank).padStart(2, "0")}</span>
    <div><strong title="${escapeHtml(label)}">${escapeHtml(label)}</strong><small>${escapeHtml(note)}</small></div>
    <p>${formatTokens(item.usage.totalTokens)}</p>
    <small title="精确值">${escapeHtml(detail)}</small>
  </article>`;
}

function dayRankingTable(items) {
  return `<table class="ranking-table">
    <thead><tr><th>排名</th><th>日期</th><th>总 token</th><th>输入</th><th>输出</th><th>缓存输入细分</th><th>会话</th></tr></thead>
    <tbody>${items.map((day, index) => `<tr>
      <td>${rankingNumber(index)}</td>
      <td><strong>${formatUsageDate(day.date)}</strong></td>
      <td><strong>${formatTokens(day.usage.totalTokens)}</strong><br><small>${formatExact(day.usage.totalTokens)}</small></td>
      <td>${formatTokens(day.usage.inputTokens)}</td>
      <td>${formatTokens(day.usage.outputTokens)}</td>
      <td>${formatTokens(day.usage.cachedInputTokens)}</td>
      <td>${day.sessionCount}</td>
    </tr>`).join("")}</tbody>
  </table>`;
}

function sessionRankingTable(items) {
  return `<table class="ranking-table session-ranking-table">
    <thead><tr><th>排名</th><th>会话</th><th>模型</th><th>总 token</th><th>输入</th><th>输出</th><th>最后活动</th><th>操作</th></tr></thead>
    <tbody>${items.map((session, index) => `<tr>
      <td>${rankingNumber(index)}</td>
      <td class="session-cell"><strong>${escapeHtml(visibleRankingSessionName(session))}</strong><small>${escapeHtml(privacyMask ? String(session.sessionId).slice(0, 8) : session.sessionId)}</small></td>
      <td>${escapeHtml(session.model)}<br><small>${escapeHtml(session.reasoningEffort)}</small></td>
      <td><strong>${formatTokens(session.usage.totalTokens)}</strong><br><small>${formatExact(session.usage.totalTokens)}</small></td>
      <td>${formatTokens(session.usage.inputTokens)}</td>
      <td>${formatTokens(session.usage.outputTokens)}</td>
      <td>${formatTime(session.lastActivityAt)}</td>
      <td><button class="detail-button" type="button" data-action="details" data-session-id="${escapeHtml(session.sessionId)}">详情</button></td>
    </tr>`).join("")}</tbody>
  </table>`;
}

function projectRankingTable(items) {
  return `<table class="ranking-table project-ranking-table">
    <thead><tr><th>排名</th><th>项目</th><th>总 token</th><th>参考成本</th><th>会话</th><th>模型</th><th>输入</th><th>输出</th><th>最后活动</th></tr></thead>
    <tbody>${items.map((project, index) => `<tr>
      <td>${rankingNumber(index)}</td>
      <td class="session-cell"><strong>${escapeHtml(visibleProjectName(project, index))}</strong><small>${escapeHtml(privacyMask ? "项目路径已隐藏" : project.projectPath || "未记录工作目录")}</small></td>
      <td><strong>${formatTokens(project.usage.totalTokens)}</strong><br><small>${formatExact(project.usage.totalTokens)}</small></td>
      <td><strong>${escapeHtml(formatProjectCost(project))}</strong></td>
      <td>${project.sessionCount}</td>
      <td>${project.modelCount}</td>
      <td>${formatTokens(project.usage.inputTokens)}</td>
      <td>${formatTokens(project.usage.outputTokens)}</td>
      <td>${formatTime(project.lastActivityAt)}</td>
    </tr>`).join("")}</tbody>
  </table>`;
}

function formatProjectCost(project) {
  if (project.estimatedCostUsd === 0 && project.unpricedTokens > 0) return "无法计价";
  return `${formatUsd(project.estimatedCostUsd)}${project.unpricedTokens > 0 ? ` · ${formatTokens(project.unpricedTokens)} 未计价` : ""}`;
}

function rankingNumber(index) {
  const rank = index + 1;
  return `<span class="ranking-number ${rank <= 3 ? `top rank-${rank}` : ""}">${String(rank).padStart(2, "0")}</span>`;
}

function visibleRankingSessionName(session) {
  return privacyMask ? String(session.sessionId).slice(0, 8) : session.displayName;
}

function visibleProjectName(project, index) {
  return privacyMask ? `项目 ${String(index + 1).padStart(2, "0")}` : project.displayName;
}

function renderSettings() {
  if (!state.settings) return;
  $("#active-window").value = state.settings.activeWindowMinutes;
  $("#title-retention").value = state.settings.titleRetentionDays;
  $("#capture-titles").checked = state.settings.captureTitles;
  if (state.startup) {
    $("#startup-enabled").checked = state.startup.isEnabled;
    $("#startup-enabled").disabled = !state.startup.canEnable;
    $("#startup-note").textContent = state.startup.canEnable ? "启用后以隐藏模式进入托盘" : "开发构建不写入 Windows 启动项";
  }
  const pricing = state.pricing;
  if (pricing) {
    const enabled = pricing.sources.filter((source) => source.enabled);
    const sourceCopy = enabled.length
      ? enabled.map((source) => `${source.displayName}：${source.lastSuccessAt ? `成功于 ${formatTime(source.lastSuccessAt)}` : gateLabel(source.gateStatus)}`).join("<br>")
      : "远程来源均未启用，当前使用内置默认参考价。";
    $("#pricing-settings-status").innerHTML = `<strong class="${pricing.isStale ? "stale" : ""}">${pricing.isRefreshing ? "正在检查..." : pricing.isStale ? "价格源未更新" : "价格源正常"}</strong><span>${sourceCopy}</span><small>下次自动检查：${pricing.nextAutomaticCheckAt ? formatTime(pricing.nextAutomaticCheckAt) : "待安排"}</small>`;
    $("#pricing-refresh").disabled = pricing.isRefreshing;
  }
}

function hasUnsavedAnnotationChanges() {
  const form = $("#session-annotation-form");
  const session = state.sessions.find((item) => item.sessionId === state.selectedSessionId);
  if (!form || !session) return false;
  const annotation = session.annotation || {
    taskType: "unclassified",
    outcome: "unmarked",
    humanMinutes: null,
    reworkReason: null
  };
  return form.elements.taskType.value !== annotation.taskType ||
    form.elements.outcome.value !== annotation.outcome ||
    form.elements.humanMinutes.value !== String(annotation.humanMinutes ?? "") ||
    form.elements.reworkReason.value !== (annotation.reworkReason ?? "");
}

async function refreshAll(showFeedback = false) {
  try {
    const [dashboard, sessions, models, usage, rankings, skills, settings, startup] = await Promise.all([
      api("/api/dashboard"), api("/api/sessions"), api("/api/models"), api("/api/usage?days=14"), api("/api/rankings?limit=100"), api("/api/skills?confidence=high"), api("/api/settings"), api("/api/startup")
    ]);
    const selectedUsageDate = state.selectedUsageDate && usage.days.some((day) => day.date === state.selectedUsageDate)
      ? state.selectedUsageDate
      : usage.today.date;
    const selectedProjectDate = state.selectedProjectDate || usage.today.date;
    const [usageAnalysis, dailyProjects] = await Promise.all([
      api(`/api/usage/analysis?date=${encodeURIComponent(selectedUsageDate)}`),
      api(`/api/rankings/projects/daily?date=${encodeURIComponent(selectedProjectDate)}`)
    ]);
    Object.assign(state, {
      dashboard, sessions, models, usage, usageAnalysis, selectedUsageDate,
      rankings, dailyProjects, selectedProjectDate, skills, settings, startup,
      pricing: usage.costEstimate?.pricingStatus || null
    });
    renderHealth();
    renderOverview();
    renderSessions();
    renderModels();
    renderUsage();
    renderRankings();
    renderSkills();
    renderSettings();
    if ($("#session-detail-dialog").open && !state.annotationEditing && !hasUnsavedAnnotationChanges()) renderSessionDetail();
    if (showFeedback) showNotice("数据已刷新");
  } catch (error) {
    showNotice(`无法读取监控数据：${error.message}`, true);
  }
}

async function saveSessionAnnotation(event) {
  event.preventDefault();
  const form = event.target;
  const values = new FormData(form);
  const humanMinutes = String(values.get("humanMinutes") || "").trim();
  const submitted = {
    taskType: values.get("taskType"),
    outcome: values.get("outcome"),
    humanMinutes: humanMinutes === "" ? null : Number(humanMinutes),
    reworkReason: values.get("reworkReason") || null
  };

  try {
    const annotation = await api(`/api/sessions/${encodeURIComponent(state.selectedSessionId)}/annotation`, {
      method: "PUT",
      body: JSON.stringify(submitted)
    });
    const session = state.sessions.find((item) => item.sessionId === state.selectedSessionId);
    if (session) session.annotation = annotation;
    state.usageAnalysis = await api(`/api/usage/analysis?date=${encodeURIComponent(state.selectedUsageDate)}`);
    state.annotationEditing = false;
    renderUsage();
    renderSessionDetail();
    showNotice("任务结果已保存");
  } catch (error) {
    showNotice(`保存任务结果失败：${error.message}`, true);
  }
}

async function selectUsageDate(date) {
  if (!state.usage?.days.some((day) => day.date === date)) return;
  state.selectedUsageDate = date;
  try {
    state.usageAnalysis = await api(`/api/usage/analysis?date=${encodeURIComponent(date)}`);
    renderUsage();
  } catch (error) {
    showNotice(`无法分析所选日期：${error.message}`, true);
  }
}

function switchView(view) {
  state.view = view;
  $("#page-title").textContent = pageHeadlines[view];
  $("#page-path").textContent = `本机监控 / ${viewLabels[view]}`;
  $$(".nav-item").forEach((item) => item.classList.toggle("active", item.dataset.view === view));
  $$(".view").forEach((item) => item.classList.toggle("active", item.id === `view-${view}`));
  window.location.hash = view;
}

async function saveSettings(event) {
  event.preventDefault();
  const submitted = {
    activeWindowMinutes: Number($("#active-window").value),
    titleRetentionDays: Number($("#title-retention").value),
    captureTitles: $("#capture-titles").checked
  };
  try {
    state.settings = await api("/api/settings", { method: "PUT", body: JSON.stringify(submitted) });
    const requestedStartup = $("#startup-enabled").checked;
    if (state.startup?.canEnable && requestedStartup !== state.startup.isEnabled) {
      state.startup = await api("/api/startup", { method: "POST", body: JSON.stringify({ enabled: requestedStartup }) });
    }
    renderSettings();
    showNotice("设置已保存");
  } catch (error) {
    showNotice(`保存失败：${error.message}`, true);
  }
}

async function refreshPricing() {
  const button = $("#pricing-refresh");
  button.disabled = true;
  try {
    state.pricing = await api("/api/pricing/refresh", { method: "POST" });
    await refreshAll();
    const enabledSource = state.pricing?.sources?.some((source) => source.enabled);
    showNotice(enabledSource ? "官方价格同步已完成" : "远程价格源未启用，已保留内置参考价");
  } catch (error) {
    showNotice(`价格检查失败：${error.message}`, true);
  } finally {
    button.disabled = false;
  }
}

function handleSessionAction(event) {
  const detailButton = event.target.closest('[data-action="details"]');
  if (detailButton) {
    openSessionDetail(detailButton.dataset.sessionId);
    return;
  }
}

function handleUsageAction(event) {
  const dateButton = event.target.closest("[data-analysis-date]");
  if (dateButton) {
    selectUsageDate(dateButton.dataset.analysisDate);
    return;
  }
  handleSessionAction(event);
}

function handleUsageChange(event) {
  if (event.target.id === "usage-analysis-date") {
    selectUsageDate(event.target.value);
  }
}

async function handleRankingAction(event) {
  const modeButton = event.target.closest("[data-ranking-mode]");
  if (modeButton) {
    state.rankingMode = modeButton.dataset.rankingMode;
    renderRankings();
    return;
  }

  const rangeButton = event.target.closest("[data-project-range]");
  if (rangeButton) {
    state.projectRankingRange = rangeButton.dataset.projectRange;
    renderRankings();
    return;
  }

  const dateButton = event.target.closest("[data-project-date-action]");
  if (dateButton && state.dailyProjects) {
    const action = dateButton.dataset.projectDateAction;
    const date = action === "today"
      ? state.dailyProjects.latestDate
      : addIsoDays(state.selectedProjectDate, action === "previous" ? -1 : 1);
    await selectProjectRankingDate(date);
    return;
  }

  const expandButton = event.target.closest("[data-project-expand-all]");
  if (expandButton && state.dailyProjects) {
    state.expandedProjectKeys = expandButton.dataset.projectExpandAll === "true"
      ? new Set(state.dailyProjects.projects.map(projectIdentityKey))
      : new Set();
    renderRankings();
    return;
  }

  const projectButton = event.target.closest("[data-project-key]");
  if (projectButton) {
    const key = projectButton.dataset.projectKey;
    if (state.expandedProjectKeys.has(key)) state.expandedProjectKeys.delete(key);
    else state.expandedProjectKeys.add(key);
    renderRankings();
    return;
  }
  handleSessionAction(event);
}

function handleRankingChange(event) {
  if (event.target.id === "project-ranking-date") {
    selectProjectRankingDate(event.target.value);
  }
}

async function destructiveAction(path, question, successMessage) {
  if (!window.confirm(question)) return;
  try {
    await api(path, { method: "DELETE" });
    await refreshAll();
    showNotice(successMessage);
  } catch (error) {
    showNotice(`操作失败：${error.message}`, true);
  }
}

async function initialize() {
  try {
    const bootstrap = await api("/api/bootstrap");
    state.controlToken = bootstrap.controlToken;
    $$(".nav-item").forEach((item) => item.addEventListener("click", () => switchView(item.dataset.view)));
    $("#refresh-button").addEventListener("click", async () => {
      await api("/api/refresh", { method: "POST" });
      window.setTimeout(() => refreshAll(true), 300);
    });
    $("#session-search").addEventListener("input", renderSessions);
    $("#session-filter").addEventListener("change", renderSessions);
    $("#session-token-sort").addEventListener("click", toggleSessionTokenSort);
    $("#overview-content").addEventListener("click", handleSessionAction);
    $("#session-table-body").addEventListener("click", handleSessionAction);
    $("#usage-content").addEventListener("click", handleUsageAction);
    $("#usage-content").addEventListener("change", handleUsageChange);
    $("#view-rankings").addEventListener("click", handleRankingAction);
    $("#view-rankings").addEventListener("change", handleRankingChange);
    $("#skills-content").addEventListener("click", (event) => {
      const button = event.target.closest("[data-skill-name]");
      if (button) openSkillDetail(button.dataset.skillName);
    });
    $("#skill-days").addEventListener("change", refreshSkills);
    $("#skill-confidence").addEventListener("change", refreshSkills);
    $("#skill-status").addEventListener("change", renderSkills);
    $("#skill-model").addEventListener("change", refreshSkills);
    $("#session-detail-close").addEventListener("click", () => $("#session-detail-dialog").close());
    $("#session-detail-dialog").addEventListener("click", (event) => {
      if (event.target === event.currentTarget) event.currentTarget.close();
    });
    $("#session-detail-dialog").addEventListener("input", (event) => {
      if (event.target.closest("#session-annotation-form")) state.annotationEditing = true;
    });
    $("#session-detail-dialog").addEventListener("change", (event) => {
      if (event.target.closest("#session-annotation-form")) state.annotationEditing = true;
    });
    $("#session-detail-dialog").addEventListener("close", () => {
      state.selectedSessionId = null;
      state.sessionQuestions = null;
      state.sessionQuestionsLoading = false;
      state.sessionQuestionsError = "";
      state.annotationEditing = false;
    });
    $("#session-detail-dialog").addEventListener("submit", (event) => {
      if (event.target.id === "session-annotation-form") saveSessionAnnotation(event);
    });
    $("#settings-form").addEventListener("submit", saveSettings);
    $("#pricing-refresh").addEventListener("click", refreshPricing);
    $("#clear-titles").addEventListener("click", () => destructiveAction("/api/titles", "清除监控数据库中的全部会话标题？token 数字不会删除。", "标题已清除"));
    $("#clear-history").addEventListener("click", () => destructiveAction("/api/history", "清除监控器保存的全部会话历史？Codex 原始文件不会删除。", "监控历史已清除"));
    const initialView = window.location.hash.slice(1);
    switchView(viewLabels[initialView] ? initialView : "overview");
    await refreshAll();
    window.setInterval(refreshAll, 3000);
  } catch (error) {
    showNotice(`监控器启动失败：${error.message}`, true);
  }
}

initialize();

const prefetchOrder = ["overview", "usage", "sessions", "models", "rankings", "skills", "settings"];
const loadedVersionKeys = new Map();
const viewLoads = new Map();
let prefetchController = null;
let prefetchRunning = false;
let pollTimer = null;

function viewVersionKey(view, versions = state.dataVersions) {
  if (!versions) return "unknown";
  const keys = {
    overview: [versions.overview, versions.usage],
    usage: [versions.usage],
    sessions: [versions.sessions],
    models: [versions.models],
    rankings: [versions.rankings, versions.usage],
    skills: [versions.skills],
    settings: [versions.settings]
  };
  return (keys[view] || []).join(":");
}

function renderView(view) {
  if (view === "overview") { renderHealth(); renderOverview(); }
  else if (view === "usage") renderUsage();
  else if (view === "sessions") renderSessions();
  else if (view === "models") renderModels();
  else if (view === "rankings") renderRankings();
  else if (view === "skills") renderSkills();
  else if (view === "settings") renderSettings();
}

async function loadViewData(view, signal, sequential = false) {
  const request = (path) => api(path, { signal });
  if (view === "overview") {
    const dashboard = await request("/api/dashboard");
    const usage = await request("/api/usage?days=14");
    const selectedUsageDate = state.selectedUsageDate && usage.days.some((day) => day.date === state.selectedUsageDate)
      ? state.selectedUsageDate
      : usage.today.date;
    const usageAnalysis = await request(`/api/usage/analysis?date=${encodeURIComponent(selectedUsageDate)}`);
    Object.assign(state, {
      dashboard,
      usage,
      usageAnalysis,
      selectedUsageDate,
      pricing: usage.costEstimate?.pricingStatus || state.pricing
    });
    if (state.sessions.length === 0) state.sessions = dashboard.sessions || [];
    return;
  }

  if (view === "usage") {
    const usage = await request("/api/usage?days=14");
    const selectedUsageDate = state.selectedUsageDate && usage.days.some((day) => day.date === state.selectedUsageDate)
      ? state.selectedUsageDate
      : usage.today.date;
    const usageAnalysis = await request(`/api/usage/analysis?date=${encodeURIComponent(selectedUsageDate)}`);
    Object.assign(state, {
      usage,
      usageAnalysis,
      selectedUsageDate,
      pricing: usage.costEstimate?.pricingStatus || state.pricing
    });
    return;
  }

  if (view === "sessions") {
    state.sessions = await request("/api/sessions");
    return;
  }

  if (view === "models") {
    state.models = await request("/api/models");
    return;
  }

  if (view === "rankings") {
    if (!state.usage) state.usage = await request("/api/usage?days=14");
    state.selectedProjectDate ||= state.usage.today.date;
    state.rankings = await request("/api/rankings?limit=100");
    state.dailyProjects = await request(`/api/rankings/projects/daily?date=${encodeURIComponent(state.selectedProjectDate)}`);
    return;
  }

  if (view === "skills") {
    state.skills = await request("/api/skills?confidence=high");
    return;
  }

  if (view === "settings") {
    state.settings = await request("/api/settings");
    state.startup = await request("/api/startup");
    state.pricing = await request("/api/pricing");
    state.rebuild = await request("/api/rebuild");
  }
}

async function loadView(view, render, force = false, signal = undefined, sequential = false) {
  const targetVersion = viewVersionKey(view);
  if (!force && state.loadedViews.has(view) && loadedVersionKeys.get(view) === targetVersion) {
    if (render) renderView(view);
    return;
  }

  if (viewLoads.has(view)) {
    await viewLoads.get(view);
    if (render && state.view === view) renderView(view);
    return;
  }

  const operation = loadViewData(view, signal, sequential)
    .then(() => {
      state.loadedViews.add(view);
      loadedVersionKeys.set(view, viewVersionKey(view));
      if (render && state.view === view) renderView(view);
      if ($("#session-detail-dialog").open && !state.annotationEditing && !hasUnsavedAnnotationChanges()) {
        renderSessionDetail();
      }
    })
    .finally(() => viewLoads.delete(view));
  viewLoads.set(view, operation);
  await operation;
}

async function requestViewLoad(view, force = false) {
  if (!viewLabels[view]) return;
  if (prefetchController) prefetchController.abort();
  try {
    await loadView(view, true, force);
    void runSequentialPrefetch();
  } catch (error) {
    if (error.name === "AbortError") {
      await loadView(view, true, force);
      void runSequentialPrefetch();
    } else {
      showNotice(`无法读取${viewLabels[view]}：${error.message}`, true);
    }
  }
}

async function refreshCurrentView(showFeedback = false, force = false) {
  try {
    await loadView(state.view, true, force);
    if (showFeedback) showNotice("当前页面已刷新");
  } catch (error) {
    showNotice(`无法读取监控数据：${error.message}`, true);
  }
}

async function runSequentialPrefetch() {
  if (prefetchRunning || document.hidden) return;
  prefetchRunning = true;
  try {
    for (const view of prefetchOrder) {
      if (view === state.view || state.loadedViews.has(view) || document.hidden) continue;
      prefetchController = new AbortController();
      try {
        await loadView(view, false, false, prefetchController.signal, true);
      } catch (error) {
        if (error.name !== "AbortError") throw error;
      } finally {
        prefetchController = null;
      }
    }
  } catch (error) {
    showNotice(`后台预载暂停：${error.message}`, true);
  } finally {
    prefetchRunning = false;
  }
}

async function pollDataVersions() {
  if (document.hidden) return;
  try {
    const next = await api("/api/data-version");
    const currentViewChanged = state.dataVersions && viewVersionKey(state.view, next) !== viewVersionKey(state.view);
    state.dataVersions = next;
    for (const view of prefetchOrder) {
      if (loadedVersionKeys.get(view) !== viewVersionKey(view, next)) state.loadedViews.delete(view);
    }
    if (currentViewChanged) await loadView(state.view, true, true);
  } catch (error) {
    showNotice(`版本检查失败：${error.message}`, true);
  } finally {
    scheduleVersionPoll();
  }
}

function scheduleVersionPoll() {
  window.clearTimeout(pollTimer);
  if (!document.hidden) pollTimer = window.setTimeout(pollDataVersions, 3000);
}

async function startRefreshCoordinator() {
  state.dataVersions = await api("/api/data-version");
  await loadView(state.view, true, true);
  scheduleVersionPoll();
  void runSequentialPrefetch();
}

document.addEventListener("visibilitychange", () => {
  if (document.hidden) {
    window.clearTimeout(pollTimer);
    if (prefetchController) prefetchController.abort();
    return;
  }

  void pollDataVersions();
  void runSequentialPrefetch();
});

namespace CodexTokenMonitor.Tests;

public sealed class DashboardDisplayContractTests
{
    [Fact]
    public void Dashboard_UsesDesktopConsoleWithMobileFallback()
    {
        var script = File.ReadAllText(FindRepositoryFile(
            "src", "CodexTokenMonitor.App", "wwwroot", "app.js"));
        var markup = File.ReadAllText(FindRepositoryFile(
            "src", "CodexTokenMonitor.App", "wwwroot", "index.html"));
        var styles = File.ReadAllText(FindRepositoryFile(
            "src", "CodexTokenMonitor.App", "wwwroot", "styles.css"));

        Assert.Contains("class=\"app-shell desktop-console\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"page-path\"", markup, StringComparison.Ordinal);
        Assert.Contains("本机只读监控", markup, StringComparison.Ordinal);
        Assert.Contains("今天的消耗，先看压力", script, StringComparison.Ordinal);
        Assert.Contains("每天用了多少，一眼看清", script, StringComparison.Ordinal);
        Assert.Contains("同一口径，对比模型消耗", script, StringComparison.Ordinal);
        Assert.Contains("class=\"overview-today-panel\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"overview-live-panel\"", script, StringComparison.Ordinal);
        Assert.Contains("html { min-width: 1180px;", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(360px, 1.3fr) minmax(245px, 0.8fr) minmax(220px, 0.7fr)", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("@media (max-width: 760px)", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("@media (max-width: 480px)", styles, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 900px)", styles, StringComparison.Ordinal);
        Assert.Contains(".app-shell { display: block;", styles, StringComparison.Ordinal);
        Assert.Contains("data-view=\"skills\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"view-skills\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"skill-days\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"skill-confidence\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"skill-status\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"skill-model\"", markup, StringComparison.Ordinal);
        Assert.Contains("/api/skills?confidence=high", script, StringComparison.Ordinal);
        Assert.Contains("function renderSkills()", script, StringComparison.Ordinal);
        Assert.Contains("function renderSkillDetail()", script, StringComparison.Ordinal);
        Assert.Contains("skill.estimatedCostUsd", script, StringComparison.Ordinal);
        Assert.Contains("item.estimatedCostUsd", script, StringComparison.Ordinal);
        Assert.Contains(".skill-table { min-width: 1050px; }", styles, StringComparison.Ordinal);
        Assert.Contains(".skills-toolbar { grid-template-columns: 1fr; }", styles, StringComparison.Ordinal);
        Assert.Contains(".skill-version-grid, .skill-detail-columns { grid-template-columns: 1fr; }", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_DistinguishesCumulativeUsageFromCurrentContext()
    {
        var script = File.ReadAllText(FindRepositoryFile(
            "src", "CodexTokenMonitor.App", "wwwroot", "app.js"));
        var markup = File.ReadAllText(FindRepositoryFile(
            "src", "CodexTokenMonitor.App", "wwwroot", "index.html"));
        var styles = File.ReadAllText(FindRepositoryFile(
            "src", "CodexTokenMonitor.App", "wwwroot", "styles.css"));
        var server = File.ReadAllText(FindRepositoryFile(
            "src", "CodexTokenMonitor.App", "WebDashboardServer.cs"));
        var program = File.ReadAllText(FindRepositoryFile(
            "src", "CodexTokenMonitor.App", "Program.cs"));
        var tray = File.ReadAllText(FindRepositoryFile(
            "src", "CodexTokenMonitor.App", "TrayApplicationContext.cs"));
        var publish = File.ReadAllText(FindRepositoryFile("scripts", "Publish.ps1"));

        Assert.Contains("会话累计处理量", script, StringComparison.Ordinal);
        Assert.Contains("不等于当前上下文占用", script, StringComparison.Ordinal);
        Assert.Contains("当前上下文估算 token", script, StringComparison.Ordinal);
        Assert.Contains("formatExact(latest.totalTokens)", script, StringComparison.Ordinal);
        Assert.Contains("formatExact(session.modelContextWindow)", script, StringComparison.Ordinal);
        Assert.Contains("data-action=\"details\"", script, StringComparison.Ordinal);
        Assert.Contains("function renderSessionDetail()", script, StringComparison.Ordinal);
        Assert.Contains("累计处理量细分", script, StringComparison.Ordinal);
        Assert.Contains("api(\"/api/usage?days=14\")", script, StringComparison.Ordinal);
        Assert.Contains("今日处理量", script, StringComparison.Ordinal);
        Assert.Contains("Token 目录价估算", script, StringComparison.Ordinal);
        Assert.Contains("官方覆盖", script, StringComparison.Ordinal);
        Assert.Contains("/api/pricing/refresh", script, StringComparison.Ordinal);
        Assert.Contains("pricing-refresh", markup, StringComparison.Ordinal);
        Assert.Contains("立即同步官方价格", markup, StringComparison.Ordinal);
        Assert.Contains("estimate.weekly", script, StringComparison.Ordinal);
        Assert.Contains("estimate.monthly", script, StringComparison.Ordinal);
        Assert.Contains("[\"每日\", \"日 token\", estimate.daily]", script, StringComparison.Ordinal);
        Assert.Contains("[\"每周\", \"周 token\", estimate.weekly]", script, StringComparison.Ordinal);
        Assert.Contains("[\"每月\", \"月 token\", estimate.monthly]", script, StringComparison.Ordinal);
        Assert.DoesNotContain(">周期 token<", script, StringComparison.Ordinal);
        Assert.Contains("不是 Codex Desktop、ChatGPT 订阅或账户实际扣款", script, StringComparison.Ordinal);
        Assert.Contains("formatUsd(period.estimatedCostUsd)", script, StringComparison.Ordinal);
        Assert.Contains("period.unpricedTokens", script, StringComparison.Ordinal);
        Assert.Contains("session.cost?.isPriced", script, StringComparison.Ordinal);
        Assert.Contains("会话 API 等值参考成本", script, StringComparison.Ordinal);
        Assert.Contains("逐问题成本明细", script, StringComparison.Ordinal);
        Assert.Contains("question.modelCallCount", script, StringComparison.Ordinal);
        Assert.Contains("只读源文件 · 不写入数据库", script, StringComparison.Ordinal);
        Assert.Contains("privacyMask ? `第 ${question.sequence} 个问题", script, StringComparison.Ordinal);
        Assert.Contains("/api/sessions/{sessionId}/questions", server, StringComparison.Ordinal);
        Assert.Contains("database.GetSessionCostsAsync", server, StringComparison.Ordinal);
        Assert.Contains(".session-cost-line", styles, StringComparison.Ordinal);
        Assert.Contains("class=\"overview-snapshot\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"hero-card\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("style=\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("page-kicker", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("LIVE MONITOR", script, StringComparison.Ordinal);
        Assert.Contains("function widthLevel(percent)", script, StringComparison.Ordinal);
        Assert.Contains(".question-detail-row", styles, StringComparison.Ordinal);
        Assert.Contains(".question-token-grid", styles, StringComparison.Ordinal);
        Assert.Contains("最近 14 天", script, StringComparison.Ordinal);
        Assert.Contains("相比前一天", script, StringComparison.Ordinal);
        Assert.Contains("相比前 7 日平均", script, StringComparison.Ordinal);
        Assert.Contains("当日 Top 5 会话", script, StringComparison.Ordinal);
        Assert.Contains("usage-analysis-date", script, StringComparison.Ordinal);
        Assert.Contains("/api/usage/analysis?date=", script, StringComparison.Ordinal);
        Assert.Contains("data-analysis-date", script, StringComparison.Ordinal);
        Assert.Contains("usage-bar-level-${level}", script, StringComparison.Ordinal);
        Assert.Contains(".usage-analysis-card", styles, StringComparison.Ordinal);
        Assert.Contains(".usage-estimate-card", styles, StringComparison.Ordinal);
        Assert.Contains(".analysis-contribution-row", styles, StringComparison.Ordinal);
        Assert.Contains("analysis-share-level-${level}", script, StringComparison.Ordinal);
        Assert.Contains("任务结果", script, StringComparison.Ordinal);
        Assert.Contains("已标记覆盖率", script, StringComparison.Ordinal);
        Assert.Contains("已完成任务平均 token", script, StringComparison.Ordinal);
        Assert.Contains("返工 token 占比", script, StringComparison.Ordinal);
        Assert.Contains("session-annotation-form", script, StringComparison.Ordinal);
        Assert.Contains("/annotation`,", script, StringComparison.Ordinal);
        Assert.Contains("!state.annotationEditing", script, StringComparison.Ordinal);
        Assert.Contains("function hasUnsavedAnnotationChanges()", script, StringComparison.Ordinal);
        Assert.Contains("state.annotationEditing = true", script, StringComparison.Ordinal);
        Assert.Contains("dialog.scrollTop = 0", script, StringComparison.Ordinal);
        Assert.Contains(".annotation-form", styles, StringComparison.Ordinal);
        Assert.Contains(".analysis-result-grid", styles, StringComparison.Ordinal);
        Assert.Contains(".analysis-share-level-20 { width: 100%; }", styles, StringComparison.Ordinal);
        Assert.Contains(".usage-bar-level-0 { height: 0; }", styles, StringComparison.Ordinal);
        Assert.Contains(".usage-bar-level-20 { height: 187px; }", styles, StringComparison.Ordinal);
        Assert.Contains("data-view=\"usage\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"view-usage\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"session-token-sort\"", markup, StringComparison.Ordinal);
        Assert.Contains("aria-sort=\"none\"", markup, StringComparison.Ordinal);
        Assert.Contains("function toggleSessionTokenSort()", script, StringComparison.Ordinal);
        Assert.Contains("state.sessionTokenSort === \"desc\" ? \"asc\" : \"desc\"", script, StringComparison.Ordinal);
        Assert.Contains("Number(left.totalUsage.totalTokens) - Number(right.totalUsage.totalTokens)", script, StringComparison.Ordinal);
        Assert.Contains(".table-sort-button", styles, StringComparison.Ordinal);
        Assert.Contains("data-view=\"rankings\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"view-rankings\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-ranking-mode=\"days\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-ranking-mode=\"sessions\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-ranking-mode=\"projects\"", markup, StringComparison.Ordinal);
        Assert.Contains("api(\"/api/rankings?limit=100\")", script, StringComparison.Ordinal);
        Assert.Contains("function renderRankings()", script, StringComparison.Ordinal);
        Assert.Contains("function dayRankingTable(items)", script, StringComparison.Ordinal);
        Assert.Contains("function sessionRankingTable(items)", script, StringComparison.Ordinal);
        Assert.Contains("function projectRankingTable(items)", script, StringComparison.Ordinal);
        Assert.Contains("function renderProjectRankings(root, rankings)", script, StringComparison.Ordinal);
        Assert.Contains("function dailyProjectGroup(project, index, dailyTotal)", script, StringComparison.Ordinal);
        Assert.Contains("function dailyProjectSessionTable(project)", script, StringComparison.Ordinal);
        Assert.Contains("data-project-range=\"daily\"", script, StringComparison.Ordinal);
        Assert.Contains("data-project-date-action=\"previous\"", script, StringComparison.Ordinal);
        Assert.Contains("data-project-expand-all=\"true\"", script, StringComparison.Ordinal);
        Assert.Contains("state.projectExpansionDate === data.date", script, StringComparison.Ordinal);
        Assert.Contains("/api/rankings/projects/daily?date=", script, StringComparison.Ordinal);
        Assert.Contains("formatProjectCost(project)", script, StringComparison.Ordinal);
        Assert.Contains("project.unpricedTokens", script, StringComparison.Ordinal);
        Assert.Contains("项目路径已隐藏", script, StringComparison.Ordinal);
        Assert.Contains(".project-ranking-table", styles, StringComparison.Ordinal);
        Assert.Contains(".daily-project-group", styles, StringComparison.Ordinal);
        Assert.Contains(".daily-project-session-table", styles, StringComparison.Ordinal);
        Assert.Contains(".project-date-toolbar", styles, StringComparison.Ordinal);
        Assert.Contains("session.lastEventAt", script, StringComparison.Ordinal);
        Assert.Contains("session.lastActivityAt", script, StringComparison.Ordinal);
        Assert.Contains(".ranking-podium", styles, StringComparison.Ordinal);
        Assert.Contains("/api/rankings", server, StringComparison.Ordinal);
        Assert.Contains("/api/rankings/projects/daily", server, StringComparison.Ordinal);
        Assert.Contains("future_date", server, StringComparison.Ordinal);
        Assert.DoesNotContain("最近一次调用", script, StringComparison.Ordinal);
        Assert.DoesNotContain("data-action=\"mute\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("cumulative-thresholds", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("context-thresholds", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("仅静音", markup, StringComparison.Ordinal);
        Assert.Contains("/api/sessions/{sessionId}/annotation", server, StringComparison.Ordinal);
        Assert.Contains("HasValidControlToken(request)", server, StringComparison.Ordinal);
        Assert.Contains("invalid_annotation_value", server, StringComparison.Ordinal);
        Assert.Contains("UnexpectedlyStopped", server, StringComparison.Ordinal);
        Assert.Contains("ApplicationStopped.Register", server, StringComparison.Ordinal);
        Assert.Contains("CodexTokenMonitor.Activate", program, StringComparison.Ordinal);
        Assert.Contains("activationEvent.Set()", program, StringComparison.Ordinal);
        Assert.Contains("RegisterWaitForSingleObject", tray, StringComparison.Ordinal);
        Assert.Contains("OpenDashboard();", tray, StringComparison.Ordinal);
        Assert.Contains("OnWebServerUnexpectedlyStopped", tray, StringComparison.Ordinal);
        Assert.Contains("Application.Exit();", tray, StringComparison.Ordinal);
        Assert.Contains("ShutdownTimeout", tray, StringComparison.Ordinal);
        Assert.Contains("Task.WhenAll", tray, StringComparison.Ordinal);
        Assert.Contains("WaitAsync(ShutdownTimeout)", tray, StringComparison.Ordinal);
        Assert.Contains("ForcedExitDelay", tray, StringComparison.Ordinal);
        Assert.Contains("Environment.Exit(0)", tray, StringComparison.Ordinal);
        Assert.Contains("exit.Click += (_, _) => ExitFromTray();", tray, StringComparison.Ordinal);
        Assert.Contains("if ($LASTEXITCODE -ne 0)", publish, StringComparison.Ordinal);
        Assert.Contains("Release tests failed", publish, StringComparison.Ordinal);
        Assert.Contains("Release publish failed", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"eyebrow\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"eyebrow\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("SESSION CONTROL ROOM", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("DAILY ANALYSIS", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CURRENT CONTEXT ESTIMATE", script, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCAL ONLY · LOOPBACK", markup, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodexTokenMonitor.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. segments]);
    }
}

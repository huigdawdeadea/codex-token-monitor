using System.Security.Cryptography;
using System.Text;
using CodexTokenMonitor.Core.Models;
using CodexTokenMonitor.Core.Monitoring;
using CodexTokenMonitor.Core.Parsing;
using CodexTokenMonitor.Core.Pricing;
using CodexTokenMonitor.Core.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodexTokenMonitor.App;

internal sealed class WebDashboardServer(
    MonitorDatabase database,
    CodexSessionMonitor monitor,
    MonitorHealthTracker health,
    StartupRegistrationService startupRegistration)
{
    private static readonly HashSet<string> TaskTypes =
    [
        "unclassified", "development", "debugging", "research",
        "documentation", "planning", "other"
    ];

    private static readonly HashSet<string> Outcomes =
    [
        "unmarked", "completed", "partial", "rework", "failed", "abandoned"
    ];

    private static readonly HashSet<string> ReworkReasons =
    [
        "requirement_change", "model_misunderstanding", "test_defect",
        "environment_issue", "other"
    ];

    private readonly string _controlToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SessionQuestionDetailReader _questionDetailReader = new(new RolloutEventParser());
    private WebApplication? _application;
    private int _stopping;

    public event Action? UnexpectedlyStopped;

    public string Url { get; private set; } = "http://127.0.0.1";

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_application is not null)
        {
            return;
        }

        var options = new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        };
        var builder = WebApplication.CreateBuilder(options);
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; style-src 'self'; script-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'";
            await next();
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();

        MapApi(app);
        app.MapFallbackToFile("index.html");
        app.Lifetime.ApplicationStopped.Register(() =>
        {
            if (Volatile.Read(ref _stopping) == 0)
            {
                UnexpectedlyStopped?.Invoke();
            }
        });
        await app.StartAsync(cancellationToken);

        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;
        Url = addresses?.SingleOrDefault(address => address.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The local dashboard did not bind to the loopback interface.");
        _application = app;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_application is null)
        {
            return;
        }

        Interlocked.Exchange(ref _stopping, 1);
        await _application.StopAsync(cancellationToken);
        await _application.DisposeAsync();
        _application = null;
    }

    private void MapApi(WebApplication app)
    {
        app.MapGet("/api/bootstrap", () => Results.Ok(new
        {
            controlToken = _controlToken,
            version = typeof(WebDashboardServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"
        }));

        app.MapGet("/api/dashboard", async (CancellationToken cancellationToken) =>
        {
            var settings = await database.GetSettingsAsync(cancellationToken);
            var sessions = await database.ListSessionsAsync(100, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var activityWindow = TimeSpan.FromMinutes(settings.ActiveWindowMinutes);
            var projected = sessions.Select(session => ProjectSession(session, now, activityWindow)).ToArray();
            return Results.Ok(new
            {
                generatedAt = now,
                health = health.GetSnapshot(),
                recent = projected.FirstOrDefault(),
                activeCount = projected.Count(session => session.IsActive),
                totalSessions = projected.Length,
                sessions = projected.Take(8),
                settings
            });
        });

        app.MapGet("/api/sessions", async (CancellationToken cancellationToken) =>
        {
            var settings = await database.GetSettingsAsync(cancellationToken);
            var sessions = await database.ListSessionsAsync(1000, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var window = TimeSpan.FromMinutes(settings.ActiveWindowMinutes);
            return Results.Ok(sessions.Select(session => ProjectSession(session, now, window)));
        });

        app.MapGet("/api/sessions/{sessionId}/questions", async (
            string sessionId,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var session = await database.GetSessionAsync(sessionId, cancellationToken);
            if (session is null)
            {
                return Results.NotFound(new { error = "session_not_found" });
            }

            return Results.Ok(await _questionDetailReader.ReadAsync(
                session.SessionId,
                session.RolloutPath,
                session.Model ?? "unknown",
                limit ?? 200,
                cancellationToken));
        });

        app.MapPut("/api/sessions/{sessionId}/annotation", async (
            HttpRequest request,
            string sessionId,
            SessionAnnotationRequest submitted,
            CancellationToken cancellationToken) =>
        {
            if (!HasValidControlToken(request))
            {
                return Results.Unauthorized();
            }

            var taskType = submitted.TaskType?.Trim().ToLowerInvariant() ?? "";
            var outcome = submitted.Outcome?.Trim().ToLowerInvariant() ?? "";
            var reworkReason = string.IsNullOrWhiteSpace(submitted.ReworkReason)
                ? null
                : submitted.ReworkReason.Trim().ToLowerInvariant();
            if (!TaskTypes.Contains(taskType) || !Outcomes.Contains(outcome))
            {
                return Results.BadRequest(new { error = "invalid_annotation_value" });
            }

            if (submitted.HumanMinutes is < 0 or > 10080 ||
                reworkReason is not null && !ReworkReasons.Contains(reworkReason))
            {
                return Results.BadRequest(new { error = "invalid_annotation_value" });
            }

            var annotation = new SessionAnnotation(
                taskType,
                outcome,
                submitted.HumanMinutes,
                reworkReason,
                DateTimeOffset.UtcNow);
            return await database.SaveSessionAnnotationAsync(sessionId, annotation, cancellationToken)
                ? Results.Ok(annotation)
                : Results.NotFound(new { error = "session_not_found" });
        });

        app.MapGet("/api/models", async (CancellationToken cancellationToken) =>
            Results.Ok(await database.GetModelSummariesAsync(cancellationToken)));

        app.MapGet("/api/usage", async (int? days, CancellationToken cancellationToken) =>
            Results.Ok(await database.GetUsageSummaryAsync(days ?? 14, cancellationToken)));

        app.MapGet("/api/usage/analysis", async (string? date, CancellationToken cancellationToken) =>
        {
            var selectedDate = DateOnly.FromDateTime(DateTime.Now);
            if (date is not null && !DateOnly.TryParseExact(date, "yyyy-MM-dd", out selectedDate))
            {
                return Results.BadRequest(new { error = "invalid_date" });
            }

            return Results.Ok(await database.GetDailyUsageAnalysisAsync(selectedDate, cancellationToken: cancellationToken));
        });

        app.MapGet("/api/rankings", async (int? limit, CancellationToken cancellationToken) =>
            Results.Ok(await database.GetUsageRankingsAsync(limit ?? 100, cancellationToken)));

        app.MapGet("/api/settings", async (CancellationToken cancellationToken) =>
            Results.Ok(await database.GetSettingsAsync(cancellationToken)));

        app.MapGet("/api/startup", () => Results.Ok(startupRegistration.GetStatus()));

        app.MapPut("/api/settings", async (
            HttpRequest request,
            MonitorSettings submitted,
            CancellationToken cancellationToken) =>
        {
            if (!HasValidControlToken(request))
            {
                return Results.Unauthorized();
            }

            var settings = submitted.Normalize();
            await database.SaveSettingsAsync(settings, cancellationToken);
            monitor.RequestRefresh();
            return Results.Ok(settings);
        });

        app.MapPost("/api/refresh", (HttpRequest request) =>
        {
            if (!HasValidControlToken(request))
            {
                return Results.Unauthorized();
            }

            monitor.RequestRefresh();
            return Results.Accepted();
        });

        app.MapPost("/api/startup", (HttpRequest request, StartupRequest body) =>
        {
            if (!HasValidControlToken(request))
            {
                return Results.Unauthorized();
            }

            var status = startupRegistration.SetEnabled(body.Enabled);
            return body.Enabled && !status.CanEnable
                ? Results.BadRequest(new { error = status.Reason, status })
                : Results.Ok(status);
        });

        app.MapDelete("/api/titles", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            if (!HasValidControlToken(request))
            {
                return Results.Unauthorized();
            }

            await database.DeleteAllTitlesAsync(cancellationToken);
            return Results.NoContent();
        });

        app.MapDelete("/api/history", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            if (!HasValidControlToken(request))
            {
                return Results.Unauthorized();
            }

            await database.ClearHistoryAsync(cancellationToken);
            return Results.NoContent();
        });
    }

    private bool HasValidControlToken(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("X-Monitor-Control", out var candidate))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(_controlToken);
        var candidateBytes = Encoding.UTF8.GetBytes(candidate.ToString());
        return expectedBytes.Length == candidateBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes);
    }

    private static SessionDto ProjectSession(
        SessionSnapshot session,
        DateTimeOffset now,
        TimeSpan activityWindow)
    {
        var model = session.Model ?? "unknown";
        return new SessionDto(
            session.SessionId,
            string.IsNullOrWhiteSpace(session.Title)
                ? session.SessionId[..Math.Min(8, session.SessionId.Length)]
                : session.Title,
            model,
            session.ReasoningEffort ?? "unknown",
            session.TotalUsage,
            session.LastUsage,
            UsageCostEstimator.EstimateModel(model, session.TotalUsage),
            session.ModelContextWindow,
            session.EstimatedContextPercent,
            session.LastActivityAt,
            session.IsActive(now, activityWindow),
            session.DataStatus,
            session.ErrorCode,
            session.LastSuccessAt,
            session.Annotation);
    }

    private sealed record StartupRequest(bool Enabled);

    private sealed record SessionAnnotationRequest(
        string? TaskType,
        string? Outcome,
        int? HumanMinutes,
        string? ReworkReason);

    private sealed record SessionDto(
        string SessionId,
        string DisplayName,
        string Model,
        string ReasoningEffort,
        TokenUsage TotalUsage,
        TokenUsage LastUsage,
        ModelUsageCost Cost,
        long ModelContextWindow,
        int? EstimatedContextPercent,
        DateTimeOffset LastActivityAt,
        bool IsActive,
        string DataStatus,
        string? ErrorCode,
        DateTimeOffset LastSuccessAt,
        SessionAnnotation Annotation);
}

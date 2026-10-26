using CodexTokenMonitor.Core.Models;

namespace CodexTokenMonitor.Core.Pricing;

/// <summary>
/// DeepSeek 官方中文价目（内置静态价，人民币元 / 百万 token）。
/// 按每条 token 事件发生的北京时间区分高峰与空闲：高峰为工作日 9:00–12:00、14:00–18:00（国定节假日除外），
/// 其余时段（含 12:00–14:00、周末、节假日全天）为空闲。2026 年使用国定节假日表；
/// 2026 年之后无法预知节假日，仅将周末视为空闲，工作日内仍按时间窗判断高峰。
/// 价目与规则来源：https://api-docs.deepseek.com/zh-cn/quick_start/pricing
/// </summary>
public static class DeepSeekPricing
{
    /// <summary>UI 与测试使用的中文来源标签。</summary>
    public const string SourceLabel = "DeepSeek 官方中文价目（内置）";

    /// <summary>价目生效日期（本机观测时间 2026-10-10）。</summary>
    public static readonly DateTimeOffset EffectiveFrom = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

    /// <summary>DeepSeek Flash 系列的已知模型别名，共用同一价档。</summary>
    public static IReadOnlyList<string> FlashAliases { get; } =
    [
        "deepseek-flash",
        "deepseek-v4.1-flash",
        "deepseek-v4-flash",
        "deepseek-v4-flash-vision-exp"
    ];

    private static readonly HashSet<string> FlashAliasSet = new(FlashAliases, StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    /// <summary>2026 年国定节假日（国务院办公厅放假安排）。调休上班日均为周末，按空闲处理即可。</summary>
    private static readonly HashSet<DateOnly> Holidays2026 = BuildHolidays2026();

    public static bool IsKnownFlashModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) && FlashAliasSet.Contains(model.Trim());

    public static ResolvedPriceComponent? TryResolve(string? model, PriceComponent component, DateTimeOffset eventAt)
    {
        if (!IsKnownFlashModel(model))
        {
            return null;
        }

        var peak = IsPeak(eventAt);
        var price = component switch
        {
            PriceComponent.Input => peak ? 2m : 1m,
            PriceComponent.CachedInput => peak ? 0.04m : 0.02m,
            PriceComponent.Output => peak ? 8m : 4m,
            _ => throw new ArgumentOutOfRangeException(nameof(component))
        };
        return new ResolvedPriceComponent(price, PriceSourceKinds.DeepSeekOfficialBuiltin, EffectiveFrom, false);
    }

    public static bool IsPeak(DateTimeOffset eventAt)
    {
        var beijing = eventAt.ToOffset(BeijingOffset);
        var date = DateOnly.FromDateTime(beijing.DateTime);
        if (date.Year == 2026 && Holidays2026.Contains(date))
        {
            return false;
        }

        if (beijing.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        var timeOfDay = beijing.TimeOfDay;
        return (timeOfDay >= TimeSpan.FromHours(9) && timeOfDay < TimeSpan.FromHours(12))
            || (timeOfDay >= TimeSpan.FromHours(14) && timeOfDay < TimeSpan.FromHours(18));
    }

    private static HashSet<DateOnly> BuildHolidays2026()
    {
        var holidays = new HashSet<DateOnly>();
        AddRange(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3));
        AddRange(new DateOnly(2026, 2, 15), new DateOnly(2026, 2, 23));
        AddRange(new DateOnly(2026, 4, 4), new DateOnly(2026, 4, 6));
        AddRange(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 5));
        AddRange(new DateOnly(2026, 6, 19), new DateOnly(2026, 6, 21));
        AddRange(new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 27));
        AddRange(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 7));
        return holidays;

        void AddRange(DateOnly start, DateOnly end)
        {
            for (var day = start; day <= end; day = day.AddDays(1))
            {
                holidays.Add(day);
            }
        }
    }
}

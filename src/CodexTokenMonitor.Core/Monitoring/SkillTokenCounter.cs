using SharpToken;

namespace CodexTokenMonitor.Core.Monitoring;

public sealed class SkillTokenCounter
{
    private readonly GptEncoding? _encoding;

    public SkillTokenCounter()
    {
        try
        {
            _encoding = GptEncoding.GetEncoding("o200k_base");
        }
        catch
        {
            _encoding = null;
        }
    }

    public (long Tokens, string Tokenizer, string Version, bool IsEstimated) Count(string content)
    {
        if (_encoding is not null)
        {
            return (_encoding.Encode(content).Count, "o200k_base", typeof(GptEncoding).Assembly.GetName().Version?.ToString() ?? "unknown", false);
        }

        // UTF-8 bytes provide a stable local fallback for mixed Chinese and code.
        return ((long)Math.Ceiling(System.Text.Encoding.UTF8.GetByteCount(content) / 3.2m), "utf8-byte-estimate", "1", true);
    }
}

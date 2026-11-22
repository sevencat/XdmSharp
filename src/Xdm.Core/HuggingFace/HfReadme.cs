using System.Text;

namespace Xdm.Core.HuggingFace;

/// <summary>
/// 把模型仓库的 <c>README.md</c> 压成一段人看的简介（模型详情窗用）。
/// HF 的 README 基本都是「YAML 头（license / tags / datasets…）+ 正文」，
/// 头对用户没用，正文里的 markdown 记号也没必要原样显示。
/// </summary>
public static class HfReadme
{
    /// <summary>简介默认截断长度；再长就只显示开头一段。</summary>
    public const int DefaultMaxChars = 400;

    /// <summary>
    /// 取 README 开头一段纯文本：去掉 YAML 头、HTML、图片、markdown 记号，空白压成单空格。
    /// 没有正文（空文件 / 只有 YAML 头）时返回 <see langword="null"/>。
    /// </summary>
    public static string? Summarize(string? markdown, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        if (maxChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChars), maxChars, "截断长度必须为正");
        }

        var body = StripFrontMatter(markdown)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var text = CollapseWhitespace(CleanMarkdown(body));
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        // 尽量在句末/空格处断开，别把单词劈成两半。
        var cut = text.LastIndexOf(' ', maxChars - 1);
        if (cut < maxChars / 2)
        {
            cut = maxChars - 1;
        }

        return text[..cut].TrimEnd(',', '，', ';', '；', ':', '：') + "…";
    }

    /// <summary>去掉开头的 <c>---</c> 包裹的 YAML 头（HF 的 model card 元数据）。</summary>
    private static string StripFrontMatter(string markdown)
    {
        var text = markdown.TrimStart('\uFEFF', ' ', '\t', '\n', '\r');
        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            return text;
        }

        // 头必须以单独一行的 --- 结束；找不到就当作没有头（README 正文可能以 --- 分隔线开头）。
        var start = text.IndexOf('\n');
        if (start < 0)
        {
            return text;
        }

        foreach (var (line, lineStart) in Lines(text, start + 1))
        {
            if (line.Trim() is "---" or "...")
            {
                var next = text.IndexOf('\n', lineStart + line.Length);
                return next < 0 ? string.Empty : text[(next + 1)..];
            }
        }

        return text;
    }

    private static IEnumerable<(string Line, int Start)> Lines(string text, int from)
    {
        var index = from;
        while (index <= text.Length)
        {
            var end = text.IndexOf('\n', index);
            if (end < 0)
            {
                yield return (text[index..], index);
                yield break;
            }

            yield return (text[index..end], index);
            index = end + 1;
        }
    }

    /// <summary>把 markdown 记号去掉，只留可读文字。</summary>
    private static string CleanMarkdown(string text)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;

        while (index < text.Length)
        {
            var ch = text[index];

            // HTML 注释 / 标签（model card 里常见 <div align="center">、<img …>）
            if (ch == '<')
            {
                var close = text.IndexOf('>', index);
                if (close > index)
                {
                    index = close + 1;
                    continue;
                }
            }

            // 图片 ![alt](url) → 丢掉整块；链接 [text](url) → 只留 text
            if (ch == '!' && index + 1 < text.Length && text[index + 1] == '[')
            {
                index = SkipLink(text, index + 1);
                continue;
            }

            if (ch == '[')
            {
                var (label, next) = ReadLinkLabel(text, index);
                if (next > index)
                {
                    builder.Append(label);
                    index = next;
                    continue;
                }
            }

            // 代码块围栏、行内反引号、表格竖线、引用符号、标题井号、星号强调
            if (ch is '`' or '|' or '*' or '#' or '>' or '~')
            {
                index++;
                continue;
            }

            builder.Append(ch);
            index++;
        }

        return builder.ToString();
    }

    private static int SkipLink(string text, int openBracket)
    {
        var (_, next) = ReadLinkLabel(text, openBracket);
        return next > openBracket ? next : openBracket + 1;
    }

    /// <summary>读 <c>[label](url)</c>：返回 label 与整段之后的下标；不是链接就原样返回。</summary>
    private static (string Label, int Next) ReadLinkLabel(string text, int openBracket)
    {
        var closeBracket = text.IndexOf(']', openBracket + 1);
        if (closeBracket < 0 || closeBracket + 1 >= text.Length || text[closeBracket + 1] != '(')
        {
            return (string.Empty, openBracket);
        }

        var closeParen = text.IndexOf(')', closeBracket + 2);
        if (closeParen < 0)
        {
            return (string.Empty, openBracket);
        }

        return (text[(openBracket + 1)..closeBracket], closeParen + 1);
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }
}

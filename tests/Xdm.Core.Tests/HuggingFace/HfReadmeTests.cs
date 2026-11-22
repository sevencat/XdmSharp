using Xdm.Core.HuggingFace;

namespace Xdm.Core.Tests.HuggingFace;

public sealed class HfReadmeTests
{
    [Fact]
    public void Summarize_StripsYamlHeader_AndHtml()
    {
        const string readme = """
            ---
            license: apache-2.0
            tags:
            - text-generation
            ---

            <div align="center">
              <h1>Qwen2.5-7B-Instruct</h1>
            </div>

            Qwen2.5 is the latest series of Qwen large language models.
            """;

        var summary = HfReadme.Summarize(readme);

        Assert.Equal("Qwen2.5-7B-Instruct Qwen2.5 is the latest series of Qwen large language models.", summary);
    }

    [Fact]
    public void Summarize_KeepsLinkText_DropsTargetsAndImages()
    {
        const string readme = """
            # 模型说明

            这是 [官方仓库](https://example.com/x) 的镜像版本。![badge](https://img.shields.io/x.svg)

            用 `transformers` 加载即可。
            """;

        var summary = HfReadme.Summarize(readme);

        Assert.Equal("模型说明 这是 官方仓库 的镜像版本。 用 transformers 加载即可。", summary);
    }

    [Fact]
    public void Summarize_TruncatesAtWordBoundary_WithEllipsis()
    {
        var readme = string.Join(' ', Enumerable.Repeat("token", 100));

        var summary = HfReadme.Summarize(readme, maxChars: 40);

        Assert.EndsWith("…", summary, StringComparison.Ordinal);
        Assert.True(summary!.Length <= 41, $"实际 {summary.Length} 字符：{summary}");
        Assert.DoesNotContain("toke…", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("---\nlicense: mit\n---\n")]
    public void Summarize_NoBody_ReturnsNull(string? readme)
    {
        Assert.Null(HfReadme.Summarize(readme));
    }

    [Fact]
    public void Summarize_NonPositiveLimit_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HfReadme.Summarize("abc", 0));
    }
}

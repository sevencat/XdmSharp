using Xdm.Core.Configuration;

namespace Xdm.Core.Tests.Configuration;

/// <summary>命令行 URL：<c>xdm-app.exe https://…</c> 必须能捞出来，且不能把 <c>--db</c> 的值当链接。</summary>
public class LaunchOptionsTests
{
    private static Func<string, string?> NoEnv => _ => null;

    [Fact]
    public void Finds_Http_Url()
    {
        Assert.Equal("https://example.com/a.bin", LaunchOptions.FindUrl(["https://example.com/a.bin"]));
    }

    [Fact]
    public void Finds_Http_Url_Alongside_Db_Argument()
    {
        var url = LaunchOptions.FindUrl(["--db", @"D:\x\xdm.db", "https://example.com/a.bin"]);

        Assert.Equal("https://example.com/a.bin", url);
    }

    [Fact]
    public void Db_Value_Is_Not_Treated_As_Url()
    {
        // --db 后面跟的是库路径，哪怕它长得像链接也不能当下载目标。
        Assert.Null(LaunchOptions.FindUrl(["--db", "https://example.com/xdm.db"]));
    }

    [Fact]
    public void Equals_Form_Db_Argument_Does_Not_Swallow_Url()
    {
        Assert.Equal("https://example.com/a.bin", LaunchOptions.FindUrl([@"--db=D:\x\xdm.db", "https://example.com/a.bin"]));
    }

    [Fact]
    public void First_Url_Wins()
    {
        var url = LaunchOptions.FindUrl(["https://a.example/1.bin", "http://b.example/2.bin"]);

        Assert.Equal("https://a.example/1.bin", url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData(@"D:\downloads\a.bin")]
    [InlineData("ftp://example.com/a.bin")]
    [InlineData("file:///D:/a.bin")]
    public void Non_Http_Values_Are_Ignored(string? value)
    {
        Assert.Null(LaunchOptions.FindUrl(value is null ? [] : [value]));
    }

    [Fact]
    public void Parse_Combines_Paths_And_Url()
    {
        var options = LaunchOptions.Parse(
            ["--db", @"D:\data\xdm.db", "https://example.com/a.bin"],
            baseDirectory: @"C:\App",
            workingDirectory: @"C:\Work",
            getEnvironment: NoEnv);

        Assert.Equal(@"D:\data\xdm.db", options.Paths.DbPath);
        Assert.Equal("https://example.com/a.bin", options.Url);
    }

    [Fact]
    public void Parse_Without_Url_Keeps_Null()
    {
        var options = LaunchOptions.Parse([], baseDirectory: @"C:\App", workingDirectory: @"C:\Work", getEnvironment: NoEnv);

        Assert.Null(options.Url);
        Assert.EndsWith(AppPaths.DbFileName, options.Paths.DbPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Still_Rejects_Bad_Db_Argument()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            LaunchOptions.Parse(["--db"], @"C:\App", @"C:\Work", NoEnv));

        Assert.Contains("--db", ex.Message, StringComparison.Ordinal);
    }
}

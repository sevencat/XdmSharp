using Xdm.Core.Configuration;

namespace Xdm.Core.Tests;

public class AppPathsTests
{
    private const string BaseDir = @"C:\App";
    private const string WorkDir = @"C:\Work";

    private static Func<string, string?> NoEnv => _ => null;

    [Fact]
    public void DbArgument_SeparateValue_Wins()
    {
        var paths = AppPaths.Resolve(["--db", @"D:\data\my.db"], BaseDir, WorkDir, NoEnv);

        Assert.Equal(@"D:\data\my.db", paths.DbPath);
        Assert.False(paths.IsFallback);
    }

    [Fact]
    public void DbArgument_EqualsForm_Wins()
    {
        var paths = AppPaths.Resolve([@"--db=D:\data\my.db"], BaseDir, WorkDir, NoEnv);

        Assert.Equal(@"D:\data\my.db", paths.DbPath);
    }

    [Fact]
    public void DbArgument_BeatsEnvironment()
    {
        var paths = AppPaths.Resolve(
            ["--db", @"D:\arg\xdm.db"],
            BaseDir,
            WorkDir,
            name => name == AppPaths.EnvDb ? @"D:\env\xdm.db" : null);

        Assert.Equal(@"D:\arg\xdm.db", paths.DbPath);
    }

    [Fact]
    public void DbArgument_MissingValue_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => AppPaths.Resolve(["--db"], BaseDir, WorkDir, NoEnv));

        Assert.Contains("--db", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DbArgument_NextIsAnotherSwitch_Throws()
    {
        Assert.Throws<ArgumentException>(() => AppPaths.Resolve(["--db", "--verbose"], BaseDir, WorkDir, NoEnv));
    }

    [Fact]
    public void DbArgument_PointsToDirectory_Throws()
    {
        var dir = Path.GetTempPath();

        Assert.Throws<ArgumentException>(() => AppPaths.Resolve(["--db", dir], BaseDir, WorkDir, NoEnv));
    }

    [Fact]
    public void DbArgument_RelativePath_ResolvedAgainstWorkingDirectory()
    {
        var paths = AppPaths.Resolve(["--db", @".\dev.db"], BaseDir, WorkDir, NoEnv);

        Assert.Equal(Path.Combine(WorkDir, "dev.db"), paths.DbPath);
    }

    [Fact]
    public void EnvDb_UsedWhenNoArgument()
    {
        var paths = AppPaths.Resolve(
            [],
            BaseDir,
            WorkDir,
            name => name == AppPaths.EnvDb ? @"D:\env\xdm.db" : null);

        Assert.Equal(@"D:\env\xdm.db", paths.DbPath);
    }

    [Fact]
    public void DataDirEnvironment_AppendsDefaultFileName()
    {
        var paths = AppPaths.Resolve(
            [],
            BaseDir,
            WorkDir,
            name => name == AppPaths.EnvDataDir ? @"D:\XdmData" : null);

        Assert.Equal(@"D:\XdmData\xdm.db", paths.DbPath);
        Assert.Equal(@"D:\XdmData\state", paths.StateDir);
    }

    [Fact]
    public void Default_UsesApplicationDirectory()
    {
        var paths = AppPaths.Resolve([], BaseDir, WorkDir, NoEnv, _ => true);

        Assert.Equal(Path.Combine(BaseDir, "xdm.db"), paths.DbPath);
        Assert.False(paths.IsFallback);
    }

    [Fact]
    public void UnwritableApplicationDirectory_FallsBackToLocalAppData()
    {
        var paths = AppPaths.Resolve(
            [],
            BaseDir,
            WorkDir,
            name => name == "LOCALAPPDATA" ? @"C:\Users\tester\AppData\Local" : null,
            canWriteDirectory: _ => false);

        Assert.Equal(@"C:\Users\tester\AppData\Local\Xdm\xdm.db", paths.DbPath);
        Assert.True(paths.IsFallback);
    }

    [Fact]
    public void StateDir_FollowsDbDirectory()
    {
        var paths = AppPaths.FromDbPath(@"D:\models\xdm.db");

        Assert.Equal(@"D:\models", paths.DataDir);
        Assert.Equal(@"D:\models\state", paths.StateDir);
    }
}

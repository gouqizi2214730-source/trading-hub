using TradingHub.App.Services;
using Xunit;

namespace TradingHub.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2.0.3", "1.2.0.3")]
    [InlineData("v1.2", "1.2.0")]
    [InlineData("v1.2.0-fork.4", "1.2.0.4")]
    public void Lee_las_versiones_de_las_etiquetas(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateChecker.Parse(tag));

    [Theory]
    [InlineData("v1.3.0-beta.1")]
    [InlineData("latest")]
    [InlineData("")]
    public void Ignora_lo_que_no_es_una_versión_estable(string tag) => Assert.Null(UpdateChecker.Parse(tag));

    [Fact]
    public void El_original_nuevo_le_gana_a_cualquier_empaquetado_del_anterior()
    {
        var installedFork = new Version(1, 2, 0, 7);
        Assert.True(UpdateChecker.IsNewer(UpdateChecker.Parse("v1.3.0")!, installedFork));
        Assert.False(UpdateChecker.IsNewer(UpdateChecker.Parse("v1.2.0")!, installedFork));
        // un empaquetado de esta copia es más nuevo que la misma versión compilada a mano (1.2.0.0)
        Assert.True(UpdateChecker.IsNewer(UpdateChecker.Parse("v1.2.0.1")!, new Version(1, 2, 0, 0)));
        Assert.Equal(new Version(1, 2, 0), UpdateChecker.Base(installedFork));
    }

    [Fact]
    public void Elige_la_versión_estable_más_nueva_con_su_instalador()
    {
        var asset = UpdateChecker.InstallerAssetName;
        var releases = new List<UpdateChecker.GhRelease>
        {
            new() { TagName = "v1.2.0.1", HtmlUrl = "a", Assets = new() { new() { Name = asset, BrowserDownloadUrl = "u1", Size = 10 } } },
            new() { TagName = "v1.2.0.3", HtmlUrl = "c", Prerelease = true },
            new() { TagName = "v1.2.0.2", HtmlUrl = "b", Assets = new() { new() { Name = asset, BrowserDownloadUrl = "u2", Size = 20, Digest = "sha256:ABCDEF" } } },
            new() { TagName = "v9.9.9", Draft = true },
        };

        var r = UpdateChecker.Pick(releases)!;
        Assert.Equal(new Version(1, 2, 0, 2), r.Version);
        Assert.Equal("u2", r.InstallerUrl);
        Assert.Equal(20, r.InstallerSize);
        Assert.Equal("abcdef", r.Sha256);
    }
}

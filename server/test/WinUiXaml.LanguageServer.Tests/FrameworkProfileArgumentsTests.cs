using System.Collections.Generic;
using WinUiXaml.LanguageServer;
using Xunit;

namespace WinUiXaml.LanguageServer.Tests;

public sealed class FrameworkProfileArgumentsTests
{
    [Theory]
    [InlineData("winui")]
    [InlineData("uno")]
    [InlineData("wpf")]
    [InlineData("librewpf")]
    [InlineData("progpu")]
    public void ParsesSupportedFramework(string profile)
    {
        Assert.Equal(profile, FrameworkProfileArguments.Parse(["--framework", profile]).Id);
    }

    [Theory]
    [InlineData("--framework")]
    [InlineData("--framework", "--other")]
    [InlineData("--framwork", "wpf")]
    [InlineData("--framework", "unknown")]
    public void DiagnosesMalformedArgumentsAndDefaultsToWinUi(params string[] arguments)
    {
        var diagnostics = new List<string>();

        var profile = FrameworkProfileArguments.Parse(arguments, diagnostics.Add);

        Assert.Same(WinUiXaml.Workspace.XamlFrameworkProfile.WinUi, profile);
        Assert.Single(diagnostics);
    }
}

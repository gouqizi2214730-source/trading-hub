using TradingHub.App.Services;
namespace TradingHub.Tests;
public class StartupOrderTests
{
    [Fact]
    public void Arranque_como_Program_Main()
    {
        // el orden de Program.Main: primero la mudanza pendiente, después las carpetas
        AppLocation.FinishPendingMove();
        Assert.False(string.IsNullOrEmpty(AppPaths.Root));
        Assert.False(string.IsNullOrEmpty(AppPaths.UserIndicators));
        Assert.False(string.IsNullOrEmpty(AppPaths.Strategies));
    }
}

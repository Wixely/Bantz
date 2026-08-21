using Bantz.Core;
using Xunit;

namespace Bantz.Core.Tests;

public sealed class InitialWindowSizeTests
{
    private static readonly InitialWindowSize Preferred = new(1170, 1300);

    [Fact]
    public void PreferredSizeIsKeptWhenItFitsTheWorkArea()
    {
        var result = InitialWindowSize.FitWithinWorkArea(Preferred, 1920, 1440);

        Assert.Equal(Preferred, result);
    }

    [Fact]
    public void SteamDeckWorkAreaScalesTheWindowDownUniformly()
    {
        var result = InitialWindowSize.FitWithinWorkArea(Preferred, 1280, 800);

        Assert.Equal(new InitialWindowSize(662, 736), result);
        Assert.True(result.Width <= 1280 - 32);
        Assert.True(result.Height <= 800 - 64);
        Assert.Equal(Preferred.Width / (double)Preferred.Height, result.Width / (double)result.Height, 2);
    }

    [Fact]
    public void WidthLimitedWorkAreaAlsoPreservesTheAspectRatio()
    {
        var result = InitialWindowSize.FitWithinWorkArea(Preferred, 640, 2000);

        Assert.True(result.Width <= 640 - 32);
        Assert.True(result.Height <= 2000 - 64);
        Assert.Equal(Preferred.Width / (double)Preferred.Height, result.Width / (double)result.Height, 2);
    }

    [Fact]
    public void InvalidWorkAreaFallsBackToThePreferredSize()
    {
        var result = InitialWindowSize.FitWithinWorkArea(Preferred, 0, 0);

        Assert.Equal(Preferred, result);
    }

    [Fact]
    public void InvalidPreferredSizeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InitialWindowSize.FitWithinWorkArea(new InitialWindowSize(0, 1300), 1280, 800));
    }
}

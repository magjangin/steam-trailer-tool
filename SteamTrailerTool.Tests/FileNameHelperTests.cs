using SteamTrailerTool.Core.Models;
using SteamTrailerTool.Core.Services;

namespace SteamTrailerTool.Tests;

public class FileNameHelperTests
{
    [Theory]
    [InlineData("Muse Dash 2 - md2_pv_0.1_kr", "Muse Dash 2 - md2_pv_0.1_kr")]
    [InlineData("A: B / C?", "A_ B _ C_")]
    [InlineData("trailing dots...  ", "trailing dots")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("", "_")]
    public void Sanitize_produces_valid_windows_names(string input, string expected)
    {
        Assert.Equal(expected, FileNameHelper.Sanitize(input));
    }

    [Fact]
    public void BuildStems_suffixes_duplicate_trailer_names_with_id()
    {
        var app = new AppTrailers(1, "Game", [
            new TrailerInfo(10, "Trailer", null, "x", null, null, null, false),
            new TrailerInfo(11, "trailer", null, "x", null, null, null, false),
            new TrailerInfo(12, "Gameplay", null, "x", null, null, null, false),
        ]);

        var stems = FileNameHelper.BuildStems(app);

        Assert.Equal("Game - Trailer (10)", stems[10]);
        Assert.Equal("Game - trailer (11)", stems[11]);
        Assert.Equal("Game - Gameplay", stems[12]);
    }
}

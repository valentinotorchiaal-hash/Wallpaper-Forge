using WallpaperForge.Core;
using Xunit;

namespace WallpaperForge.Tests;

public class OtherAppsTests
{
    [Theory]
    [InlineData(@"""C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe"" -autostart", "wallpaper-engine")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\wallpaper_engine\launcher.exe", "wallpaper-engine")]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\Lively Wallpaper\Lively.exe", "lively")]
    [InlineData(@"C:\Program Files\WindowsApps\12030rocksdanister.LivelyWallpaper_2.0.0.0_x64__97hta09mmv6hy\Lively.exe", "lively")]
    public void MatchPath_RecognizesKnownApps(string command, string expected)
        => Assert.Equal(expected, AutostartMatcher.MatchPath(command)?.Id);

    [Theory]
    [InlineData(@"C:\Program Files\Steam\steam.exe -silent")]
    [InlineData(@"C:\Users\x\AppData\Local\WallpaperForge\WallpaperForge.exe --startup")]
    [InlineData(@"C:\Program Files\Some Wallpaper Thing\wp.exe")]
    [InlineData("")]
    public void MatchPath_IgnoresOthers(string command) => Assert.Null(AutostartMatcher.MatchPath(command));

    [Fact]
    public void MatchProcess_GenericLauncherNeedsPath()
    {
        Assert.Null(AutostartMatcher.MatchProcess("launcher", @"C:\Games\Other\launcher.exe"));
        Assert.Equal("wallpaper-engine", AutostartMatcher.MatchProcess("launcher", @"C:\Steam\steamapps\common\wallpaper_engine\launcher.exe")?.Id);
        Assert.Equal("wallpaper-engine", AutostartMatcher.MatchProcess("wallpaper64", null)?.Id); // proceso elevado: sin ruta
        Assert.Equal("lively", AutostartMatcher.MatchProcess("Lively", null)?.Id);
        Assert.Null(AutostartMatcher.MatchProcess("explorer", @"C:\Windows\explorer.exe"));
    }

    [Fact]
    public void StartupApproved_FormatMatchesTaskManager()
    {
        var on = AutostartMatcher.StartupApprovedValue(true);
        var off = AutostartMatcher.StartupApprovedValue(false, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(12, on.Length);
        Assert.Equal(0x02, on[0]);
        Assert.Equal(0x03, off[0]);
        Assert.False(AutostartMatcher.IsStartupApprovedDisabled(on));
        Assert.True(AutostartMatcher.IsStartupApprovedDisabled(off));
        Assert.True(AutostartMatcher.IsStartupApprovedDisabled(new byte[] { 0x07, 0, 0, 0 })); // variante deshabilitada
        Assert.False(AutostartMatcher.IsStartupApprovedDisabled(new byte[] { 0x06, 0, 0, 0 })); // variante habilitada
        Assert.False(AutostartMatcher.IsStartupApprovedDisabled(null));
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.FromFileTimeUtc(BitConverter.ToInt64(off, 4)));
    }

    [Fact]
    public void Csv_ParsesQuotedFields()
    {
        var cols = AutostartMatcher.ParseCsvLine("\"PC\",\"\\WE\",\"N/A\",\"Listo\",\"x\",\"y\",\"0\",\"a\",\"\"\"C:\\wallpaper_engine\\wallpaper64.exe\"\" -a\",\"\",\"\",\"Habilitado\"");
        Assert.Equal(12, cols.Count);
        Assert.Equal("\\WE", cols[1]);
        Assert.Equal("\"C:\\wallpaper_engine\\wallpaper64.exe\" -a", cols[8]);
        Assert.Equal("wallpaper-engine", AutostartMatcher.MatchPath(cols[8])?.Id);
    }

    [Fact]
    public void Shortcut_MatchesByContentOrName()
    {
        var lnk = System.Text.Encoding.Unicode.GetBytes("xx C:\\Steam\\steamapps\\common\\wallpaper_engine\\launcher.exe xx");
        Assert.Equal("wallpaper-engine", AutostartMatcher.MatchShortcutBytes("algo.lnk", lnk)?.Id);
        Assert.Equal("wallpaper-engine", AutostartMatcher.MatchShortcutBytes("Wallpaper Engine.lnk", new byte[4])?.Id);
        Assert.Null(AutostartMatcher.MatchShortcutBytes("Discord.lnk", new byte[4]));
    }

    [Fact]
    public void Migration_ParsesSelectedWallpapers()
    {
        var json = """
        {
          "steamuser": {
            "general": { "x": 1 },
            "wallpaperconfig": {
              "selectedwallpapers": {
                "\\\\.\\DISPLAY2": { "file": "C:/Steam/steamapps/workshop/content/431960/111/index.html" },
                "\\\\?\\DISPLAY#GSM5B7F#5&1#0&UID4352#{e6f07b5f}": { "file": "C:/Steam/steamapps/workshop/content/431960/222/scene.json" }
              }
            }
          }
        }
        """;
        var sel = WallpaperEngineMigration.ParseConfig(json);
        Assert.Equal(2, sel.Count);
        Assert.Equal(2, sel[0].DisplayNumber);
        Assert.Null(sel[1].DisplayNumber);
        Assert.EndsWith(@"431960\111", sel[0].WallpaperFolder);

        var map = WallpaperEngineMigration.MapToMonitors(sel, new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2" });
        Assert.EndsWith(@"431960\111", map[@"\\.\DISPLAY2"]);
        Assert.EndsWith(@"431960\222", map[@"\\.\DISPLAY1"]);
    }

    [Fact]
    public void Migration_ToleratesGarbage()
    {
        Assert.Empty(WallpaperEngineMigration.ParseConfig("no es json"));
        Assert.Empty(WallpaperEngineMigration.ParseConfig("{\"selectedwallpapers\":{\"m\":{\"file\":123}}}"));
        Assert.Empty(WallpaperEngineMigration.ParseConfig("{\"a\":[1,2,{\"b\":null}]}"));
        Assert.Empty(WallpaperEngineMigration.ParseConfig("{\"selectedwallpapers\":{\"m\":{\"file\":123}}}"));
    }
}

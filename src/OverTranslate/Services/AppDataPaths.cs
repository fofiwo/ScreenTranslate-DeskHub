using System.IO;
using Velopack.Locators;

namespace OverTranslate.Services;

/// <summary>
/// 便携版把数据保存在程序旁；安装版使用独立用户目录，避免升级或重装替换程序时丢失设置。
/// </summary>
internal static class AppDataPaths
{
    /// <summary>
    /// Points <see cref="AssetsRoot"/> somewhere else, for trying a download without touching the
    /// real one. Uninstalling ignores it and always removes <see cref="DefaultAssetsRoot"/>.
    /// </summary>
    internal const string AssetsOverride = "SCREENTRANSLATE_ASSETS_DIR";

    internal const string AssetsFolderName = "assets";

    internal static string Root { get; } = ResolveRoot(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        IsInstalled());

    private static bool IsInstalled()
    {
        if (!VelopackLocator.IsCurrentSet) return false;
        var locator = VelopackLocator.Current;
        return locator.AppId == "ScreenTranslate" && !locator.IsPortable
            && locator.CurrentlyInstalledVersion is not null;
    }

    internal static string ResolveRoot(string baseDirectory, string localApplicationData, bool installed) =>
        installed ? Path.Combine(localApplicationData, "ScreenTranslate-data") : Path.Combine(baseDirectory, "data");

    internal static string Logs => Path.Combine(Root, "logs");

    internal static string Diagnostics => Path.Combine(Root, "diagnostics");

    /// <summary>当前运行模式的数据目录下的模型资源，不受测试用环境变量影响。</summary>
    internal static string DefaultAssetsRoot { get; } = Path.Combine(Root, "assets");

    internal static string AssetsRoot { get; } =
        Environment.GetEnvironmentVariable(AssetsOverride) is { Length: > 0 } overridden
            ? Path.GetFullPath(overridden)
            : DefaultAssetsRoot;

    internal static string Models => Path.Combine(AssetsRoot, "models");

    /// <summary>Native libraries downloaded on demand, one folder per library and version.</summary>
    internal static string Runtimes => Path.Combine(AssetsRoot, "runtimes");
}

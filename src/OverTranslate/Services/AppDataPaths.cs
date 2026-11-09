using System.IO;
using Velopack.Locators;

namespace OverTranslate.Services;

/// <summary>
/// 安装版使用固定用户目录；便携版默认使用程序旁的数据，本地构建可显式绑定共享目录。
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

    internal static string ResolveRoot(string baseDirectory, string localApplicationData, bool installed)
    {
        if (installed) return Path.Combine(localApplicationData, "ScreenTranslate-data");

        // 本地日常版与预览版显式绑定同一资料目录；不扫描或合并其他副本的数据。
        var profileFile = Path.Combine(baseDirectory, "data-path.txt");
        if (!File.Exists(profileFile)) return Path.Combine(baseDirectory, "data");
        var profile = File.ReadAllText(profileFile).Trim();
        if (string.IsNullOrWhiteSpace(profile))
            throw new InvalidDataException("data-path.txt 未指定数据目录，请修正后重新启动，原数据未更改。");
        return Path.GetFullPath(profile, Path.GetFullPath(baseDirectory));
    }

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

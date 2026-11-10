using System.IO;
using Velopack.Locators;

namespace OverTranslate.Services;

/// <summary>
/// 安装版使用安装根目录中的 data；便携版默认使用程序旁的数据。
/// </summary>
internal static class AppDataPaths
{
    /// <summary>
    /// Points <see cref="AssetsRoot"/> somewhere else, for trying a download without touching the
    /// real one. Uninstalling ignores it and always removes <see cref="DefaultAssetsRoot"/>.
    /// </summary>
    internal const string AssetsOverride = "SCREENTRANSLATE_ASSETS_DIR";

    internal const string AssetsFolderName = "assets";
    private static InstalledDataRecovery? _recovery;

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
        if (installed) return Path.Combine(UninstallCleanup.InstallRoot(baseDirectory), "data");

        // 本地日常版与预览版显式绑定同一资料目录；不扫描或合并其他副本的数据。
        var profileFile = Path.Combine(baseDirectory, "data-path.txt");
        if (!File.Exists(profileFile)) return Path.Combine(baseDirectory, "data");
        var profile = File.ReadAllText(profileFile).Trim();
        if (string.IsNullOrWhiteSpace(profile))
            throw new InvalidDataException("data-path.txt 未指定数据目录，请修正后重新启动，原数据未更改。");
        return Path.GetFullPath(profile, Path.GetFullPath(baseDirectory));
    }

    internal static void Initialize()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // MSIX 宿主启动的进程可能将 LocalAppData 重定向到宿主缓存，用户根目录不参与该重定向。
        var state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ScreenTranslate");
        Directory.CreateDirectory(state);
        var binding = Path.Combine(state, "profile.txt");
        var oldBinding = Path.Combine(local, "ScreenTranslate-profile.txt");
        var previousBinding = File.Exists(binding) ? binding : oldBinding;
        var previous = File.Exists(previousBinding) ? File.ReadAllText(previousBinding).Trim() : "";
        // 显式绑定到安装版资料的本地预览也必须同步恢复副本。
        if (!IsInstalled() && !string.Equals(previous, Root, StringComparison.OrdinalIgnoreCase)) return;
        var recovery = new InstalledDataRecovery(Root, InstalledDataRecovery.RecoveryRoot(Root, state));
        var legacy = Path.Combine(local, "ScreenTranslate-data");
        if (Path.IsPathFullyQualified(previous) && !string.Equals(previous, Root, StringComparison.OrdinalIgnoreCase))
        {
            var priorBackup = InstalledDataRecovery.RecoveryRoot(previous, state);
            if (Directory.Exists(previous)) legacy = previous;
            else if (Directory.Exists(priorBackup)) legacy = priorBackup;
        }
        recovery.Initialize(legacy);
        var temporary = binding + ".tmp";
        File.WriteAllText(temporary, Root);
        File.Move(temporary, binding, overwrite: true);
        _recovery = recovery;
    }

    internal static void BackupFile(string path) => _recovery?.BackupFile(path);
    internal static InstalledDataRecovery? Recovery => _recovery;

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

using System.IO;
using System.Text.Json;
using System.Windows;
using OverTranslate.Views.ClipboardHistory;

namespace OverTranslate.Services.Lan;

internal static class SyncSetupPrompt
{
    private sealed record Choice(bool EnableSync, int Version = 1);
    internal static bool NeedsPrompt(string path) => !File.Exists(path);
    internal static void SaveChoice(string path, bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Choice(enabled)));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void ShowIfNeeded()
    {
        var path = Path.Combine(AppDataPaths.Root, "sync-onboarding.json");
        if (!NeedsPrompt(path)) return;
        var dialog = new SyncSetupWindow(enabled =>
        {
            if (enabled) LanTransferWindow.OpenForSyncSetup();
            SaveChoice(path, enabled);
        });
        if (System.Windows.Application.Current.Windows.OfType<OverTranslate.Views.Shell.ShellWindow>()
            .FirstOrDefault(window => window.IsVisible) is { } owner)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        dialog.ShowDialog();
        if (dialog.Choice == MessageBoxResult.Yes) LanTransferWindow.Open();
    }
}

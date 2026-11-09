using System.IO;
using System.Text.Json;
using System.Windows;
using OverTranslate.Views.ClipboardHistory;
using MessageBox = System.Windows.MessageBox;

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
        var choice = MessageBox.Show("是否与另一台自己的电脑双向同步？\n\n同步新复制的文本、图片，以及提示词库、模型库（包括 API Key）。资料只在你确认配对的电脑之间传输。\n\n点击“是”开始配对。首次需在另一台输入本机显示的地址和配对码，并在本机确认；配对后全部所选内容自动同步，无需分别设置。\n\n“否”表示暂不使用；“取消”表示下次再询问。以后也可从“局域网同步”开启。", "设置双向同步", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;
        try
        {
            if (choice == MessageBoxResult.Yes) LanTransferWindow.OpenForSyncSetup();
            SaveChoice(path, choice == MessageBoxResult.Yes);
        }
        catch (Exception) { MessageBox.Show("同步引导尚未完成，请检查目录权限或从“局域网同步”重试。", "同步设置"); }
    }
}

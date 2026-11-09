using System.IO;
using System.Windows.Controls;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Views.ClipboardHistory;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyClipboardRecopyPosition()
    {
        var folder = Path.Combine(Path.GetTempPath(), "clipboard-recopy-" + Guid.NewGuid().ToString("N"));
        var store = new ClipboardHistoryStore(folder);
        for (var i = 0; i < 70; i++) store.Add(new() { Text = "recopy fixture " + i });
        var window = new ClipboardHistoryWindow(store, _ => Task.CompletedTask)
        { Left = -10000, Top = -10000, ShowActivated = false };
        window.SetWindowPinned(true);
        try
        {
            window.Show(); PumpUntil(() => !window.IsRefreshing); window.UpdateLayout();
            var list = (ListBox)window.FindName("HistoryList");
            var scroll = Visuals<ScrollViewer>(list).First();
            var target = (ClipboardRecordView)list.Items[60];
            list.SelectedItem = target; list.ScrollIntoView(target); window.UpdateLayout();
            Assert.True(scroll.VerticalOffset > 0);
            var copy = window.CopySelectedAsync(); PumpUntil(() => copy.IsCompleted); copy.GetAwaiter().GetResult();
            Assert.False(window.IsVisible);
            window.ShowPanel(); PumpUntil(() => !window.IsRefreshing); window.UpdateLayout();
            Assert.Equal(target.Record.Id, ((ClipboardRecordView)list.Items[0]).Record.Id);
            PumpUntil(() => scroll.VerticalOffset == 0);
            Assert.Equal(target.Record.Id, window.SelectedRecord!.Id);
        }
        finally { window.Close(); Directory.Delete(folder, true); }
    }
}

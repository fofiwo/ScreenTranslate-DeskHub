using System.Diagnostics;
using System.IO;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Services.Lan;

namespace OverTranslate.Views.ClipboardHistory;

public partial class ClipboardHistoryWindow
{
    private bool _receiveWasInTaskbar;
    private int _receivedPage;
    private const int ReceivedPageSize = 50;
    private bool HasIncomingFiles => IncomingRequestsPanel.Children.Count > 0;

    internal static async Task<string?> ConfirmIncomingFilesAsync(string peer, LanHeader offer, string initialDirectory, CancellationToken token)
    {
        if (token.IsCancellationRequested) return null;
        if (_current is null)
        {
            var version = _openVersion;
            var store = await ClipboardHistoryRuntime.StoreAsync.WaitAsync(token);
            if (token.IsCancellationRequested || version != _openVersion && _current is null) return null;
            _current ??= new ClipboardHistoryWindow(store, managePosition: true);
        }
        return await _current.ReceiveFilesAsync(peer, offer, initialDirectory, token);
    }

    private async Task<string?> ReceiveFilesAsync(string peer, LanHeader offer, string initialDirectory, CancellationToken token)
    {
        if (_closed || token.IsCancellationRequested) return null;
        if (!IsVisible || WindowState == WindowState.Minimized) _pasteTarget = OriginalApplicationPaste.CaptureTarget();
        var card = new FileReceiveCard(peer, offer, initialDirectory);
        if (!HasIncomingFiles) _receiveWasInTaskbar = ShowInTaskbar;
        IncomingRequestsPanel.Children.Add(card);
        UpdateReceiveAttention();
        using var cancellation = token.Register(() => Dispatcher.BeginInvoke(card.Reject));
        try
        {
            ReceiveErrorText.Text = "";
            _receivedPage = 0; RefreshReceivedFiles();
            ShowReceivePage();
            if (!IsVisible) { Show(); if (_managePosition) PositionWindow(); }
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            KeyboardForeground.Activate(this);
            System.Media.SystemSounds.Exclamation.Play();
            var folder = await card.Decision;
            return token.IsCancellationRequested ? null : folder;
        }
        finally
        {
            IncomingRequestsPanel.Children.Remove(card);
            if (!_closed) UpdateReceiveAttention();
        }
    }

    private void UpdateReceiveAttention()
    {
        var count = IncomingRequestsPanel.Children.Count;
        Topmost = WindowPinned || count > 0;
        ShowInTaskbar = count > 0 || _receiveWasInTaskbar;
        ReceivePageTitle.Text = count > 0 ? $"接收文件（{count} 待处理）" : "接收文件";
        ViewReceivedFilesButton.Content = count > 0 ? $"查看接收文件（{count} 待处理）" : "查看接收文件";
    }

    private void ShowReceivePage()
    {
        SyncTab.IsSelected = true;
        SendPageContent.Visibility = SendActionsPanel.Visibility = Visibility.Collapsed;
        ReceivePageContent.Visibility = Visibility.Visible;
        SendPageScroll.ScrollToTop();
    }
    private void ViewReceivedFiles_Click(object sender, RoutedEventArgs e) => ShowReceivePage();
    private void BackToSend_Click(object sender, RoutedEventArgs e) => ShowSendPage();
    private void ShowSendPage()
    {
        SyncTab.IsSelected = true;
        ReceivePageContent.Visibility = Visibility.Collapsed;
        SendPageContent.Visibility = SendActionsPanel.Visibility = Visibility.Visible;
        SendPageScroll.ScrollToTop();
    }
    private void RefreshReceivedFiles()
    {
        var all = _syncActivities.ReceivedSnapshot();
        var pages = Math.Max(1, (all.Count + ReceivedPageSize - 1) / ReceivedPageSize);
        _receivedPage = Math.Clamp(_receivedPage, 0, pages - 1);
        var received = all.Skip(_receivedPage * ReceivedPageSize).Take(ReceivedPageSize).ToArray();
        if (ReceivedFilesList.ItemsSource is not SyncActivity[] previous || !previous.SequenceEqual(received))
            ReceivedFilesList.ItemsSource = received;
        ReceivedFilesEmpty.Visibility = received.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ReceivedPageText.Text = $"共 {all.Count} 批 · 第 {_receivedPage + 1} / {pages} 页";
        PreviousReceivedPageButton.IsEnabled = _receivedPage > 0;
        NextReceivedPageButton.IsEnabled = _receivedPage + 1 < pages;
        ReceiveStorageWarning.Text = _syncActivities.ReceivedStorageWarning;
    }
    private void ReceivedPage_Click(object sender, RoutedEventArgs e)
    { _receivedPage += ReferenceEquals(sender, NextReceivedPageButton) ? 1 : -1; RefreshReceivedFiles(); SendPageScroll.ScrollToTop(); }
    private void OpenReceivedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SyncActivity activity }) return;
        ReceiveErrorText.Text = "";
        try
        {
            var folder = LanFiles.ValidateFolder(activity.Folder);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException();
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = true });
        }
        catch (Exception) { ReceiveErrorText.Text = "无法打开保存文件夹，目录可能已移动、删除或无访问权限。"; }
    }
}

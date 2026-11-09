using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Clipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace OverTranslate.Services;

internal sealed record SystemClipboardEntry(string Id, DateTimeOffset Timestamp, string Text,
    ImageSource? Image, bool HasImage, bool PreviewFailed, ClipboardHistoryItem? NativeItem = null)
{
    public string Summary => Text.Length > 0 ? Text.ReplaceLineEndings(" ") : HasImage ? "图片" : "内容暂时无法预览";
    public string TimeLabel => Timestamp.LocalDateTime.ToString("g");
}

internal sealed record SystemClipboardSnapshot(ClipboardHistoryItemsResultStatus Status,
    IReadOnlyList<SystemClipboardEntry> Items);

internal interface ISystemClipboardHistory
{
    Task<SystemClipboardSnapshot> ReadAsync(CancellationToken token);
    SetHistoryItemAsContentStatus Restore(SystemClipboardEntry entry);
}

internal sealed class SystemClipboardHistory : ISystemClipboardHistory
{
    public async Task<SystemClipboardSnapshot> ReadAsync(CancellationToken token)
    {
        var result = await Clipboard.GetHistoryItemsAsync().AsTask(token);
        if (result.Status != ClipboardHistoryItemsResultStatus.Success)
            return new(result.Status, []);

        var entries = new List<SystemClipboardEntry>();
        foreach (var item in result.Items.OrderByDescending(i => i.Timestamp))
        {
            token.ThrowIfCancellationRequested();
            var content = item.Content;
            var hasText = content.Contains(StandardDataFormats.Text);
            var hasImage = content.Contains(StandardDataFormats.Bitmap);
            if (!hasText && !hasImage) continue;
            var text = "";
            ImageSource? image = null;
            var failed = false;
            if (hasText)
            {
                try { text = await content.GetTextAsync().AsTask(token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { failed = true; }
            }
            if (hasImage)
            {
                try
                {
                    var reference = await content.GetBitmapAsync().AsTask(token);
                    using var randomAccess = await reference.OpenReadAsync().AsTask(token);
                    using var stream = randomAccess.AsStreamForRead();
                    var dimensions = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None).Frames[0];
                    stream.Position = 0;
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    if (dimensions.PixelWidth >= dimensions.PixelHeight) bitmap.DecodePixelWidth = Math.Min(256, dimensions.PixelWidth);
                    else bitmap.DecodePixelHeight = Math.Min(256, dimensions.PixelHeight);
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    image = bitmap;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { failed = true; }
            }
            entries.Add(new(item.Id, item.Timestamp, text, image, hasImage, failed, item));
        }
        return new(result.Status, entries);
    }

    public SetHistoryItemAsContentStatus Restore(SystemClipboardEntry entry) =>
        entry.NativeItem is { } item ? Clipboard.SetHistoryItemAsContent(item) : SetHistoryItemAsContentStatus.ItemDeleted;
}

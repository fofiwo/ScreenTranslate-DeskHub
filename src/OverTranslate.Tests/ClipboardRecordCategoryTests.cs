using OverTranslate.Services.ClipboardHistory;
using Xunit;

namespace OverTranslate.Tests;

public sealed class ClipboardRecordCategoryTests
{
    [Theory]
    [InlineData(".png")]
    [InlineData(".JPG")]
    [InlineData(".jpeg")]
    [InlineData(".bmp")]
    [InlineData(".gif")]
    [InlineData(".tif")]
    [InlineData(".tiff")]
    [InlineData(".webp")]
    [InlineData(".avif")]
    [InlineData(".heic")]
    [InlineData(".heif")]
    [InlineData(".svg")]
    [InlineData(".ico")]
    public void ImagePathsBelongOnlyToImagesEvenWhenSourceIsMissing(string extension)
    {
        var record = new ClipboardRecord { Files = [Path.Combine(Path.GetTempPath(), Guid.NewGuid() + extension)] };
        Assert.Equal("图片", record.Category);
        Assert.True(record.Matches("图片", extension));
        Assert.True(record.Matches("全部", extension));
        Assert.False(record.Matches("文件", ""));
        Assert.Equal("图片", (record with { Files = [.. record.Files, @"C:\fixture\other.PNG"] }).Category);
    }

    [Fact]
    public void FoldersAndMixedSelectionsRemainFilesEvenWithImageAttachment()
    {
        var folder = Path.Combine(Path.GetTempPath(), "clipboard-category-" + Guid.NewGuid() + ".png");
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var paths in new[] { new[] { folder }, new[] { @"C:\fixture\report.pdf" },
                new[] { @"C:\fixture\photo.png", @"C:\fixture\report.pdf" },
                new[] { @"C:\fixture\photo.png", folder } })
            {
                var record = new ClipboardRecord { Files = paths, HasImage = true };
                Assert.Equal("文件", record.Category);
                Assert.True(record.Matches("文件", ""));
                Assert.False(record.Matches("图片", ""));
            }
            Assert.Equal("图片", new ClipboardRecord { HasImage = true }.Category);
            Assert.Equal("文本", new ClipboardRecord { Text = "photo.png" }.Category);
        }
        finally { Directory.Delete(folder); }
    }
}

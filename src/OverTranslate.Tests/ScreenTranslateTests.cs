using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Views.Capture;
using Xunit;

namespace OverTranslate.Tests;

public class ScreenTranslateTests
{
    [Fact]
    public void CaptureCanRevealTheLiveDesktop()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var bitmap = new Bitmap(320, 180);
                var window = new ScreenCaptureWindow(bitmap, new Rectangle(0, 0, 320, 180));
                Assert.True(window.AllowsTransparency);
                window.SwitchToBackgroundMode();
                Assert.Equal(Visibility.Collapsed, ((System.Windows.Controls.Image)window.FindName("ScreenshotImage")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((System.Windows.Shapes.Path)window.FindName("DimPath")).Visibility);
                Assert.Equal(System.Windows.Media.Brushes.Transparent, window.Background);
                window.Close();
            }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void HttpCoordinatesScaleIndependentlyAndKeepColumns()
    {
        var blocks = OcrResponseParser.Parse("""{"width":200,"height":100,"blocks":[{"text":"Left","x":10,"y":10,"width":40,"height":10},{"text":"Right","x":150,"y":10,"width":40,"height":10}]}""", 400, 300, false);
        Assert.Equal(new Rect(20, 30, 80, 30), blocks[0].Bounds);
        Assert.Equal(new Rect(300, 30, 80, 30), blocks[1].Bounds);
    }

    [Fact]
    public void BaiduLocationsArePhysicalImageCoordinatesAndClipped()
    {
        var blocks = OcrResponseParser.Parse("""{"words_result":[{"words":"Edge","location":{"left":90,"top":10,"width":20,"height":10}}]}""", 100, 100, true);
        Assert.Equal(new Rect(90, 10, 10, 10), Assert.Single(blocks).Bounds);
        Assert.Throws<InvalidOperationException>(() => OcrResponseParser.Parse("""{"error_code":110,"error_msg":"secret"}""", 100, 100, true));
    }

    [Theory]
    [InlineData("http://example.com/ocr")]
    [InlineData("https://user:password@example.com/ocr")]
    [InlineData("https://example.com/ocr?key=secret")]
    [InlineData("file:///C:/temp")]
    public void UnsafeEndpointsAreRefused(string endpoint) => Assert.Throws<InvalidOperationException>(() => HttpOcrProvider.ValidateEndpoint(endpoint));
}

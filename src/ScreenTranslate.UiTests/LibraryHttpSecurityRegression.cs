using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services.Library;
using OverTranslate.Views.Library;
using Xunit;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private sealed class HttpPermissionCatalogHandler : HttpMessageHandler
    {
        public int Sent { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent++;
            Assert.Equal("http://gateway.invalid/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer fixture-only", request.Headers.Authorization!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"data\":[{\"id\":\"fixture-model\"}]}") });
        }
    }

    private static void VerifyLibraryHttpPermission(string directory)
    {
        var path = System.IO.Path.Combine(directory, "http-permission", "library.json");
        var store = new LibraryStore(path);
        var handler = new HttpPermissionCatalogHandler();
        using var http = new LibraryHttp(handler);
        var window = new LibraryWindow(store, http, copy: _ => { })
        { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        T Control<T>(string name) => (T)window.FindName(name);
        void Invoke(string method, params object[] args) => typeof(LibraryWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        void Fetch()
        {
            Control<Button>("EditorFetchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Control<Button>("EditorFetchButton").IsEnabled);
        }
        void Save()
        {
            Control<Button>("SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Control<FrameworkElement>("EditorLayer").Visibility == Visibility.Collapsed);
        }
        try
        {
            window.Show();
            Control<TabControl>("Tabs").SelectedIndex = 1;
            Invoke("NewModel_Click", window, new RoutedEventArgs());
            var permission = Control<CheckBox>("ModelInsecure");
            Assert.False(permission.IsChecked == true);
            Control<ComboBox>("PlatformBox").Text = "自定义测试";
            Control<TextBox>("EndpointBox").Text = "http://gateway.invalid/v1";
            Control<PasswordBox>("ModelKey").Password = "fixture-only";
            var ai = new OverTranslate.Views.Settings.AiOrganizingSettingsPage(() => store);
            ((CheckBox)ai.FindName("AiInsecure")).IsChecked = true;
            Fetch();
            Assert.Equal(0, handler.Sent);
            Assert.Contains("远程 HTTP", Control<TextBlock>("EditorStatus").Text);

            permission.IsChecked = true;
            Fetch();
            Assert.Equal(1, handler.Sent);
            Assert.Single(Control<ComboBox>("EditorModelIds").Items);
            Render(window, "library-http-permission", 880, 580);
            Save();
            var saved = new LibraryStore(path).Read().Connections.Single();
            Assert.True(saved.AllowInsecureEndpoint);
            Assert.Equal(["fixture-model"], saved.ModelIds);

            Invoke("NewModel_Click", window, new RoutedEventArgs());
            Assert.False(permission.IsChecked == true);
            Invoke("EditModel", saved);
            Assert.True(permission.IsChecked == true);
            Control<TextBox>("EndpointBox").Text = "http://other.invalid/v1";
            Assert.False(permission.IsChecked == true);

            Invoke("EditModel", saved);
            permission.IsChecked = false;
            Save();
            saved = new LibraryStore(path).Read().Connections.Single();
            Assert.False(saved.AllowInsecureEndpoint);
            Assert.Equal(["fixture-model"], saved.ModelIds);
            Invoke("EditModel", saved);
            Fetch();
            Assert.Equal(1, handler.Sent);
            Assert.Contains("远程 HTTP", Control<TextBlock>("EditorStatus").Text);
        }
        finally { window.CloseForTests(); }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Views.Shell;
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OverTranslate.Views.Settings;

public partial class SettingsPage : UserControl
{
    private bool _loading = true;
    private readonly Dictionary<HotkeyAction, TextBox> _boxes = new();
    private readonly Dictionary<HotkeyAction, CheckBox> _enabled = new();
    private HotkeyAction? _recording;
    private readonly Action<bool> _setHotkeyRecording;
    private readonly Func<bool> _saveSettings;

    public SettingsPage() : this(recording =>
    {
        if (System.Windows.Application.Current?.MainWindow is MainWindow main)
            main.SetHotkeyRecording(recording);
    }) { }

    internal SettingsPage(Action<bool> setHotkeyRecording, Func<bool>? saveSettings = null)
    {
        _setHotkeyRecording = setHotkeyRecording;
        _saveSettings = saveSettings ?? (() => SettingsService.Instance.TrySave());
        InitializeComponent();
        foreach (var (action, title) in new[] {
            (HotkeyAction.Capture, "截图翻译"), (HotkeyAction.RealtimePause, "持续翻译 / 暂停 / 恢复"),
            (HotkeyAction.TranslationWindow, "打开主窗口"), (HotkeyAction.OcrExtract, "OCR 提取文字并复制"),
            (HotkeyAction.LibraryWindow, "提示词与模型资料"), (HotkeyAction.ClipboardHistory, "剪贴板历史") })
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            var toggle = new CheckBox { Content = title, VerticalAlignment = VerticalAlignment.Center, Tag = action,
                IsEnabled = action != HotkeyAction.Capture };
            toggle.Checked += Enabled_Changed; toggle.Unchecked += Enabled_Changed;
            var box = new TextBox { IsReadOnly = true, Tag = action, Height = 38, VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "点击后按键盘组合，Esc 取消" };
            System.Windows.Automation.AutomationProperties.SetName(box, title + "快捷键");
            box.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (_recording is null) _setHotkeyRecording(true);
                _recording = action;
                box.Focus(); box.Text = "请按键盘组合…"; e.Handled = true;
            };
            box.PreviewKeyDown += Record_KeyDown;
            box.LostKeyboardFocus += (_, _) => { if (_recording == action) EndRecording(); };
            Grid.SetColumn(box, 1); row.Children.Add(toggle); row.Children.Add(box); HotkeyRows.Children.Add(row);
            _boxes[action] = box; _enabled[action] = toggle;
        }
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => EndRecording(reload: false);
        Reload();
    }

    public void Reload()
    {
        _loading = true;
        try
        {
            var s = SettingsService.Instance.Current;
            PreserveFormattingCheckBox.IsChecked = s.OcrPreserveFormatting;
            AllowFallbackCheckBox.IsChecked = s.AllowFallbackServices;
            AutoTranslateCheckBox.IsChecked = s.AutoTranslateAfterSelection;
            LiveOverlayCheckBox.IsChecked = s.LiveCaptureOverlay;
            VerticalCheckBox.IsChecked = s.Capture.VerticalText;
            MangaCheckBox.IsChecked = s.UseMangaModels;
            VerboseCheckBox.IsChecked = s.VerboseLogging;
            StartupCheckBox.IsChecked = StartupService.IsEnabled;
            foreach (var binding in HotkeyBindings.Resolve(s))
            {
                if (!_boxes.TryGetValue(binding.Action, out var box)) continue;
                var display = binding.Action switch {
                    HotkeyAction.Capture => s.HotkeyDisplay, HotkeyAction.RealtimePause => s.RealtimePauseHotkeyDisplay,
                    HotkeyAction.TranslationWindow => s.TranslationWindowHotkeyDisplay,
                    HotkeyAction.LibraryWindow => s.LibraryHotkeyDisplay,
                    HotkeyAction.ClipboardHistory => s.ClipboardHotkeyDisplay, _ => s.OcrHotkeyDisplay };
                box.Text = display; box.IsEnabled = binding.Enabled;
                _enabled[binding.Action].IsChecked = binding.Enabled;
            }
            RefreshHotkeyStatus();
        }
        finally { _loading = false; }
    }

    private void RefreshHotkeyStatus()
    {
        var errors = HotkeyBindings.Resolve(SettingsService.Instance.Current)
            .Where(b => b.Enabled && (b.ShadowedBy is not null || !HotkeyBindings.IsBindable(b.Trigger)))
            .Select(b => $"{_enabled[b.Action].Content}：组合冲突或不可注册，请重新设置。").ToList();
        if (System.Windows.Application.Current?.MainWindow is MainWindow main)
            errors.AddRange(main.HotkeyRegistrationFailures.Select(a => $"{_enabled[a].Content}：注册失败，组合可能被其他程序占用。"));
        HotkeyStatus.Text = errors.Count > 0 ? string.Join("\n", errors) : "键盘组合无冲突；全局注册状态以当前运行程序为准。";
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = SettingsService.Instance.Current;
        s.OcrPreserveFormatting = PreserveFormattingCheckBox.IsChecked == true;
        s.AllowFallbackServices = AllowFallbackCheckBox.IsChecked == true;
        s.LiveCaptureOverlay = LiveOverlayCheckBox.IsChecked == true;
        s.Capture.VerticalText = VerticalCheckBox.IsChecked == true;
        s.VerboseLogging = VerboseCheckBox.IsChecked == true;
        SettingsService.Instance.UpdateCaptureOptions(autoTranslate: AutoTranslateCheckBox.IsChecked == true);
        SettingsService.Instance.UpdateMangaModelOptions(useModels: MangaCheckBox.IsChecked == true);
        ShowSaveStatus(_saveSettings());
        LogLevelService.Apply(s.VerboseLogging);
    }

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try { StartupService.Set(StartupCheckBox.IsChecked == true); }
        catch (Exception) { DiagnosticsResult.Text = "开机启动设置失败，请检查系统权限。"; }
        _loading = true; StartupCheckBox.IsChecked = StartupService.IsEnabled; _loading = false;
    }

    private void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox { Tag: HotkeyAction action } box) return;
        var s = SettingsService.Instance.Current;
        bool on = box.IsChecked == true;
        switch (action) {
            case HotkeyAction.RealtimePause: s.RealtimePauseHotkeyEnabled = on; break;
            case HotkeyAction.TranslationWindow: s.TranslationWindowHotkeyEnabled = on; break;
            case HotkeyAction.OcrExtract: s.OcrHotkeyEnabled = on; break;
            case HotkeyAction.LibraryWindow: s.LibraryHotkeyEnabled = on; break;
            case HotkeyAction.ClipboardHistory: s.ClipboardHotkeyEnabled = on; break;
        }
        SaveShortcut();
    }

    private void Record_KeyDown(object sender, KeyEventArgs e)
    {
        if (_recording is not { } action || sender is not TextBox box) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { EndRecording(); return; }
        if (e.IsRepeat || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers;
        uint mods = (modifiers.HasFlag(ModifierKeys.Control) ? 2u : 0) |
            (modifiers.HasFlag(ModifierKeys.Alt) ? 1u : 0) | (modifiers.HasFlag(ModifierKeys.Shift) ? 4u : 0);
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        var trigger = ShortcutTrigger.Keyboard(mods, vk);
        var s = SettingsService.Instance.Current;
        if (vk == 0 || modifiers.HasFlag(ModifierKeys.Windows) || !HotkeyBindings.IsBindable(trigger))
        { HotkeyStatus.Text = "请使用 Ctrl、Alt 或 Shift 组合，或 F1–F24 等功能键。"; return; }
        var conflict = HotkeyBindings.Resolve(s).FirstOrDefault(b => b.Action != action && b.Enabled && b.Trigger == trigger);
        if (conflict.Enabled) { HotkeyStatus.Text = $"与“{_enabled[conflict.Action].Content}”冲突，请选择其他组合。"; return; }
        var prefix = GlobalHotkey.ModifiersToString(mods);
        var display = prefix.Length == 0 ? key.ToString() : $"{prefix}+{key}";
        switch (action) {
            case HotkeyAction.Capture: s.HotkeyModifiers = mods; s.HotkeyVirtualKey = vk; s.HotkeyDisplay = display; s.HotkeyInputKind = ShortcutInputKind.Keyboard; break;
            case HotkeyAction.RealtimePause: s.RealtimePauseHotkeyModifiers = mods; s.RealtimePauseHotkeyVirtualKey = vk; s.RealtimePauseHotkeyDisplay = display; s.RealtimePauseHotkeyInputKind = ShortcutInputKind.Keyboard; break;
            case HotkeyAction.TranslationWindow: s.TranslationWindowHotkeyModifiers = mods; s.TranslationWindowHotkeyVirtualKey = vk; s.TranslationWindowHotkeyDisplay = display; s.TranslationWindowHotkeyInputKind = ShortcutInputKind.Keyboard; break;
            case HotkeyAction.OcrExtract: s.OcrHotkeyModifiers = mods; s.OcrHotkeyVirtualKey = vk; s.OcrHotkeyDisplay = display; break;
            case HotkeyAction.LibraryWindow: s.LibraryHotkeyModifiers = mods; s.LibraryHotkeyVirtualKey = vk; s.LibraryHotkeyDisplay = display; break;
            case HotkeyAction.ClipboardHistory: s.ClipboardHotkeyModifiers = mods; s.ClipboardHotkeyVirtualKey = vk; s.ClipboardHotkeyDisplay = display; break;
        }
        SaveShortcut();
    }

    private void EndRecording(bool reload = true)
    {
        if (_recording is null) return;
        _recording = null;
        _setHotkeyRecording(false);
        if (reload) Reload();
    }

    private void SaveShortcut()
    {
        var saved = _saveSettings();
        if (_recording is not null) EndRecording(reload: false);
        else if (System.Windows.Application.Current?.MainWindow is MainWindow main) main.ReRegisterHotkey();
        ShellWindow.Current?.RefreshHotkeyHints();
        Reload();
        ShowSaveStatus(saved);
    }

    private void ShowSaveStatus(bool saved)
    {
        SaveStatus.Text = saved ? "" : "设置保存失败，本次选择仅在当前会话生效。请检查目录权限后重试保存。";
        SaveRetryBtn.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RetrySave_Click(object sender, RoutedEventArgs e) => ShowSaveStatus(_saveSettings());

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        ExportDiagnosticsBtn.IsEnabled = false;
        try { var path = await Task.Run(() => DiagnosticBundleService.Export()); DiagnosticsResult.Text = $"已导出：{path}"; }
        catch (Exception) { DiagnosticsResult.Text = "诊断包导出失败，请检查日志与目录权限。"; }
        finally { ExportDiagnosticsBtn.IsEnabled = true; }
    }

}

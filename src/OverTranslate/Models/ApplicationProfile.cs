namespace OverTranslate.Models;

public sealed class ApplicationProfile
{
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public string SourceLanguage { get; set; } = "EN";
    public string TargetLanguage { get; set; } = "ZH-HANS";
    public TranslationProvider Provider { get; set; } = TranslationProvider.Microsoft;
    public OcrProviderKind OcrProvider { get; set; }
    public string OcrEndpoint { get; set; } = "";
    public RealtimeSettings Appearance { get; set; } = new();
    public bool AutoAttach { get; set; }
    public List<ProfileRegion> Regions { get; set; } = [];
    public override string ToString() => Name + (AutoAttach ? "（自动附着）" : "");
}

public sealed class ProfileRegion
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public Services.Realtime.RealtimeBlockMode Mode { get; set; }
    public Services.Realtime.RealtimeTextOrientation Orientation { get; set; }
}

namespace OverTranslate.Models;

[Flags]
public enum NetworkFeature
{
    Updates = 1, Translation = 2, Dictionary = 4, Speech = 8,
    OnlineOcr = 16, ModelDownloads = 32, Library = 64, Diagnostics = 128,
    All = Updates | Translation | Dictionary | Speech | OnlineOcr | ModelDownloads | Library | Diagnostics,
}

public sealed class NetworkProxySettings
{
    public bool Enabled { get; set; }
    public string Address { get; set; } = "";
    public NetworkFeature Features { get; set; } = NetworkFeature.All;
}

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OverTranslate.Services.Lan;

internal sealed record LanTrustedDevice(string Id, string Name, bool Listening, string Address, int Port,
    string PairCode, LanNode.ListenIdentity? Identity, bool Clipboard, bool Library, bool AutoReconnect,
    DateTimeOffset LastConnected = default, string LastPeerAddress = "");

/// <summary>整个信任文件由 Windows 当前账户加密，身份私钥与配对凭据不写入明文。</summary>
internal sealed class LanTrustedDevices(string path)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ScreenTranslate.Lan.TrustedDevices.v1");
    private readonly object _gate = new();
    private bool _readFailed;
    public string Error { get; private set; } = "";
    public IReadOnlyList<LanTrustedDevice> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(path)) return [];
                if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException();
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    var profiles = JsonSerializer.Deserialize<LanTrustedDevice[]>(bytes) ?? [];
                    if (profiles.Length > 16 || profiles.Select(p => p.Id).Distinct().Count() != profiles.Length
                        || profiles.Any(p => !Guid.TryParseExact(p.Id, "N", out _)
                        || p.Name.Length is < 1 or > 80 || p.Port is < 1 or > 65535
                        || (p.Listening && p.Identity is null)
                        || !System.Net.IPAddress.TryParse(p.Address, out var address) || !LanNode.IsLocalAddress(address)))
                        throw new InvalidDataException();
                    _readFailed = false; Error = "";
                    return profiles;
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            catch (Exception)
            {
                _readFailed = true;
                Error = "设备信任信息无法读取或不属于当前 Windows 账户，自动重连已暂停，原文件保留。";
                return [];
            }
        }
    }
    public void Save(IEnumerable<LanTrustedDevice> profiles)
    {
        lock (_gate)
        {
            if (_readFailed) throw new InvalidDataException(Error);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(profiles.ToArray());
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(temporary, encrypted);
                File.Move(temporary, path, true);
                Error = "";
            }
            catch (Exception) { Error = "设备信任保存失败，本次连接可继续，重启后可能需要重新配对。"; throw; }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OverTranslate.Services.Library;

internal sealed record LibraryImportResult(int Prompts, int Connections, bool AiImported, int KeysVerified);

internal static class FloatFolderImporter
{
    public static string DefaultSourceDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "float-folder");

    // Chromium 130 OSCrypt: v10 + 12字节nonce + AES-GCM密文 + 16字节tag。
    // 仅在本机迁移时读取该应用的Local State，密钥转存为当前Windows用户的DPAPI格式。
    internal static string Reprotect(string encrypted, byte[]? profileKey)
    {
        if (encrypted.Length == 0) return "";
        var bytes = Convert.FromBase64String(encrypted);
        byte[] plain;
        if (bytes.AsSpan().StartsWith("v10"u8))
        {
            if (profileKey is null || bytes.Length < 31) throw new InvalidDataException("缺少原应用加密配置，无法迁移凭据。");
            plain = new byte[bytes.Length - 31];
            using var aes = new AesGcm(profileKey, 16);
            aes.Decrypt(bytes.AsSpan(3, 12), bytes.AsSpan(15, plain.Length), bytes.AsSpan(bytes.Length - 16), plain);
        }
        else plain = LocalSecretStore.UnprotectBytes(bytes);
        try
        {
            var text = Encoding.UTF8.GetString(plain);
            var protectedText = LocalSecretStore.Protect(text);
            if (LocalSecretStore.Unprotect(protectedText) != text) throw new InvalidDataException("凭据迁移校验失败，未保存资料。");
            return protectedText;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static LibraryImportResult Import(LibraryStore store, string sourceDirectory)
    {
        var configPath = Path.Combine(sourceDirectory, "config.json");
        var bytes = File.ReadAllBytes(configPath);
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        var sourceId = "FloatFolder:" + Path.GetFullPath(configPath) + ":" + fingerprint;
        if (store.Read().ImportedSources.Contains(sourceId)) return new(0, 0, false, 0);
        using var config = JsonDocument.Parse(bytes);
        var root = config.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            (root.TryGetProperty("prompts", out var promptData) && promptData.ValueKind != JsonValueKind.Array) ||
            (root.TryGetProperty("modelConnections", out var connectionData) && connectionData.ValueKind != JsonValueKind.Array))
            throw new InvalidDataException("原资料列表格式无效，未迁入部分数据或写入导入标记。");
        byte[]? profileKey = null;
        var localState = Path.Combine(sourceDirectory, "Local State");
        try
        {
            if (File.Exists(localState))
            {
                using var state = JsonDocument.Parse(File.ReadAllBytes(localState));
                if (state.RootElement.TryGetProperty("os_crypt", out var osCrypt))
                {
                    var key = Convert.FromBase64String(LibraryHttp.Text(osCrypt, "encrypted_key"));
                    if (!key.AsSpan().StartsWith("DPAPI"u8)) throw new InvalidDataException("不支持原应用的凭据格式，未修改目标资料。");
                    profileKey = LocalSecretStore.UnprotectBytes(key[5..]);
                }
            }
            var prompts = new List<LibraryPrompt>();
            var connections = new List<LibraryConnection>();
            long Time(JsonElement item, string name) => item.TryGetProperty(name, out var t) && t.TryGetInt64(out var value) ? value : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (root.TryGetProperty("prompts", out var promptArray) && promptArray.ValueKind == JsonValueKind.Array)
            foreach (var p in promptArray.EnumerateArray())
            {
                var id = LibraryHttp.Text(p, "id");
                if (id.Length == 0) throw new InvalidDataException("原提示词缺少ID，未修改目标资料。");
                prompts.Add(new() { Id = "floatfolder:" + id, Title = LibraryHttp.Text(p, "title"), Description = LibraryHttp.Text(p, "description"),
                    Content = LibraryHttp.Text(p, "content"), CreatedAt = Time(p, "createdAt"), UpdatedAt = Time(p, "updatedAt"),
                    Tags = p.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array ? tags.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [] });
            }
            int keys = 0;
            if (root.TryGetProperty("modelConnections", out var modelArray) && modelArray.ValueKind == JsonValueKind.Array)
            foreach (var c in modelArray.EnumerateArray())
            {
                var id = LibraryHttp.Text(c, "id");
                if (id.Length == 0) throw new InvalidDataException("原模型资料缺少ID，未修改目标资料。");
                var encrypted = LibraryHttp.Text(c, "apiKeyEncrypted");
                connections.Add(new() { Id = "floatfolder:" + id, Platform = LibraryHttp.Text(c, "platform"), ModelType = LibraryHttp.Text(c, "modelType"),
                    Endpoint = LibraryHttp.Text(c, "endpoint"), Model = LibraryHttp.Text(c, "model"), Note = LibraryHttp.Text(c, "note"),
                    RemainingBalance = LibraryHttp.Text(c, "remainingBalance"), CreatedAt = Time(c, "createdAt"), UpdatedAt = Time(c, "updatedAt"),
                    ApiKeyProtected = Reprotect(encrypted, profileKey) });
                if (encrypted.Length > 0) keys++;
            }
            var aiKey = LibraryHttp.Text(root, "aiApiKeyEncrypted");
            var ai = new LibraryAiSettings { Endpoint = LibraryHttp.Text(root, "aiEndpoint"), Model = LibraryHttp.Text(root, "aiModel"),
                ApiKeyProtected = Reprotect(aiKey, profileKey), AutoSummarize = !root.TryGetProperty("autoSummarizePrompts", out var automatic) || automatic.ValueKind != JsonValueKind.False,
                AllowInsecureEndpoint = root.TryGetProperty("allowInsecureEndpoint", out var insecure) && insecure.ValueKind == JsonValueKind.True };
            if (ai.Endpoint.Length == 0) ai.Endpoint = "https://api.openai.com/v1";
            if (aiKey.Length > 0) keys++;
            var addedPrompts = 0; var addedConnections = 0; bool aiImported = false;
            store.Update(s =>
            {
                if (s.ImportedSources.Contains(sourceId)) return;
                foreach (var p in prompts) if (s.Prompts.All(x => x.Id != p.Id)) { s.Prompts.Add(p); addedPrompts++; }
                foreach (var c in connections) if (s.Connections.All(x => x.Id != c.Id)) { s.Connections.Add(c); addedConnections++; }
                if (s.Ai.Model.Length == 0 && s.Ai.ApiKeyProtected.Length == 0) { s.Ai = ai; aiImported = true; }
                s.ImportedSources.Add(sourceId);
            });
            return new(addedPrompts, addedConnections, aiImported, keys);
        }
        finally { if (profileKey is not null) CryptographicOperations.ZeroMemory(profileKey); }
    }
}

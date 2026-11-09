using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OverTranslate.Services;
using OverTranslate.Services.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed class FloatFolderImporterTests
{
    [Theory]
    [InlineData("{\"prompts\":\"corrupt-array\",\"modelConnections\":[]}")]
    [InlineData("{\"prompts\":[],\"modelConnections\":[{\"id\":\"bad\",\"apiKeyEncrypted\":\"not-base64\"}]}")]
    public void InvalidSourceCannotCommitPartialDataOrAnImportMarker(string source)
    {
        var directory = Path.Combine(Path.GetTempPath(), "float-import-invalid-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var destination = Path.Combine(directory, "destination", "library.json");
            var store = new LibraryStore(destination);
            store.SavePrompt(new() { Content = "Keep existing target data." });
            var previous = File.ReadAllBytes(destination);
            File.WriteAllText(Path.Combine(directory, "config.json"), source);
            Assert.ThrowsAny<Exception>(() => FloatFolderImporter.Import(store, directory));
            Assert.Equal(previous, File.ReadAllBytes(destination));
            Assert.Empty(store.Read().ImportedSources);
            Assert.Equal(source, File.ReadAllText(Path.Combine(directory, "config.json")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ImportPreservesAllFieldsReencryptsKeysAndDoesNotDuplicateOnRepeat()
    {
        var directory = Path.Combine(Path.GetTempPath(), "float-import-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            const string rawKey = "0123456789abcdef0123456789abcdef";
            var key = Encoding.UTF8.GetBytes(rawKey);
            var protectedKey = Convert.FromBase64String(LocalSecretStore.Protect(rawKey));
            File.WriteAllText(Path.Combine(directory, "Local State"), JsonSerializer.Serialize(new { os_crypt = new { encrypted_key = Convert.ToBase64String("DPAPI"u8.ToArray().Concat(protectedKey).ToArray()) } }));
            var plain = Encoding.UTF8.GetBytes("fixture-secret-not-real");
            var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[plain.Length]; var tag = new byte[16];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
            var encrypted = Convert.ToBase64String("v10"u8.ToArray().Concat(nonce).Concat(cipher).Concat(tag).ToArray());
            var source = JsonSerializer.Serialize(new { prompts = new[] { new { id = "p1", title = "原始标题", description = "描述", content = "正文\n完整保留", tags = new[] { "标签" }, createdAt = 100L, updatedAt = 200L } },
                modelConnections = new[] { new { id = "c1", platform = "平台", modelType = "image", endpoint = "https://example.invalid/v1", model = "model-id", apiKeyEncrypted = encrypted, remainingBalance = "10.50", note = "备注", createdAt = 300L, updatedAt = 400L } },
                aiEndpoint = "https://ai.invalid/v1", aiModel = "summary-model", aiApiKeyEncrypted = encrypted, autoSummarizePrompts = false });
            File.WriteAllText(Path.Combine(directory, "config.json"), source);
            var destination = Path.Combine(directory, "destination", "library.json");
            var store = new LibraryStore(destination);
            var result = FloatFolderImporter.Import(store, directory);
            Assert.Equal(new LibraryImportResult(1, 1, true, 2), result);
            var state = new LibraryStore(destination).Read();
            Assert.Equal("正文\n完整保留", state.Prompts.Single().Content);
            Assert.Equal(100L, state.Prompts.Single().CreatedAt);
            Assert.Equal("10.50", state.Connections.Single().RemainingBalance);
            Assert.Equal("备注", state.Connections.Single().Note);
            Assert.Equal("image", state.Connections.Single().ModelType);
            Assert.Equal("fixture-secret-not-real", LocalSecretStore.Unprotect(state.Connections.Single().ApiKeyProtected));
            Assert.Equal("fixture-secret-not-real", LocalSecretStore.Unprotect(state.Ai.ApiKeyProtected));
            Assert.False(state.Ai.AutoSummarize);
            Assert.DoesNotContain("fixture-secret-not-real", File.ReadAllText(destination));
            Assert.Equal(new LibraryImportResult(0, 0, false, 0), FloatFolderImporter.Import(store, directory));
            Assert.Single(store.Read().Prompts);
            Assert.Equal(source, File.ReadAllText(Path.Combine(directory, "config.json")));
        }
        finally { Directory.Delete(directory, true); }
    }
}

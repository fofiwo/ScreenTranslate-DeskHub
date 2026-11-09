using System.Net;
using System.Net.Http;
using System.Text;
using OverTranslate.Services.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LibraryHttpTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("http://gateway.invalid/v1")]
    [InlineData("http://192.168.1.50:8080/v1")]
    public async Task RemoteHttpCatalogIsRejectedBeforeSending(string endpoint)
    {
        var sent = 0;
        using var client = new LibraryHttp(new Handler(_ =>
        {
            sent++;
            return Json("{\"data\":[{\"id\":\"fixture-model\"}]}");
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListModelsAsync(new()
        {
            Endpoint = endpoint,
            ApiKeyProtected = OverTranslate.Services.LocalSecretStore.Protect("fixture-only")
        }));
        Assert.Equal(0, sent);
    }

    [Theory]
    [InlineData("https://gateway.invalid/v1")]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://[::1]:11434/v1")]
    public async Task HttpsAndLoopbackCatalogsNeedNoInsecurePermission(string endpoint)
    {
        var sent = 0;
        using var client = new LibraryHttp(new Handler(request =>
        {
            sent++;
            Assert.Equal(new Uri(endpoint + "/models"), request.RequestUri);
            Assert.Equal("Bearer fixture-only", request.Headers.Authorization!.ToString());
            return Json("{\"data\":[{\"id\":\"fixture-model\"}]}");
        }));
        Assert.Equal(["fixture-model"], await client.ListModelsAsync(new()
        {
            Endpoint = endpoint,
            ApiKeyProtected = OverTranslate.Services.LocalSecretStore.Protect("fixture-only")
        }));
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task RemoteHttpPermissionPersistsAndCanBeRevokedWithoutLosingData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "library-http-permission-" + Guid.NewGuid());
        try
        {
            var path = Path.Combine(directory, "library.json");
            var store = new LibraryStore(path);
            var connection = store.SaveConnection(new()
            {
                Platform = "Fixture", Endpoint = "http://192.168.1.50:8080/v1", Model = "fixed",
                AllowInsecureEndpoint = true
            }, "fixture-only", ["cached-model"]);
            var sent = 0;
            using var client = new LibraryHttp(new Handler(request =>
            {
                sent++;
                Assert.Equal("Bearer fixture-only", request.Headers.Authorization!.ToString());
                return Json("{\"data\":[{\"id\":\"fetched-model\"}]}");
            }));
            connection = new LibraryStore(path).Read().Connections.Single();
            Assert.True(connection.AllowInsecureEndpoint);
            await store.RefreshModelsAsync(connection.Id, client.ListModelsAsync);
            connection = store.Read().Connections.Single();
            var protectedKey = connection.ApiKeyProtected;
            connection.AllowInsecureEndpoint = false;
            store.SaveConnection(connection);

            var reopened = new LibraryStore(path);
            connection = reopened.Read().Connections.Single();
            Assert.False(connection.AllowInsecureEndpoint);
            Assert.Equal(protectedKey, connection.ApiKeyProtected);
            Assert.Equal(["fetched-model"], connection.ModelIds);
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.RefreshModelsAsync(connection.Id, client.ListModelsAsync));
            Assert.Equal(1, sent);
            Assert.Equal(["fetched-model"], reopened.Read().Connections.Single().ModelIds);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("http://name:fixture@gateway.invalid/v1")]
    [InlineData("http://gateway.invalid/v1?route=fixture")]
    [InlineData("http://gateway.invalid/v1#fragment")]
    public async Task InsecurePermissionDoesNotAllowCredentialsOrRoutingParameters(string endpoint)
    {
        var sent = 0;
        using var client = new LibraryHttp(new Handler(_ =>
        {
            sent++;
            return Json("{\"data\":[]}");
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListModelsAsync(new()
        {
            Endpoint = endpoint, AllowInsecureEndpoint = true
        }));
        Assert.Equal(0, sent);
    }

    [Theory]
    [InlineData("https://api.anthropic.com/v1", "/v1/models?limit=100", "x-api-key", "{\"data\":[{\"id\":\"claude-fixture\"}]}", "claude-fixture")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta", "/v1beta/models?pageSize=100", "x-goog-api-key", "{\"models\":[{\"name\":\"models/gemini-fixture\"}]}", "gemini-fixture")]
    [InlineData("http://localhost:11434/api", "/api/tags", "Authorization", "{\"models\":[{\"name\":\"local-fixture\"}]}", "local-fixture")]
    [InlineData("https://gateway.invalid/v1", "/v1/models", "Authorization", "{\"data\":[{\"id\":\"gateway-fixture\"}]}", "gateway-fixture")]
    [InlineData("https://api.cohere.ai/v2", "/v1/models?page_size=100&endpoint=chat", "Authorization", "{\"models\":[{\"name\":\"cohere-fixture\"}]}", "cohere-fixture")]
    [InlineData("https://api.together.xyz/v1", "/v1/models", "Authorization", "[{\"id\":\"together-fixture\",\"type\":\"chat\"}]", "together-fixture")]
    [InlineData("https://api.cloudflare.com/client/v4/accounts/fixture-account/ai/v1", "/client/v4/accounts/fixture-account/ai/models/search?per_page=100&page=1", "Authorization", "{\"result\":[{\"name\":\"cloudflare-fixture\"}],\"result_info\":{\"total_pages\":1}}", "cloudflare-fixture")]
    public async Task CatalogUsesTheProtocolOfTheActualEndpoint(string endpoint, string path, string header, string response, string expected)
    {
        using var client = new LibraryHttp(new Handler(request =>
        {
            Assert.Equal(path, request.RequestUri!.PathAndQuery);
            Assert.True(request.Headers.Contains(header));
            return Json(response);
        }));
        var models = await client.ListModelsAsync(new() { Endpoint = endpoint, ModelType = "chat", ApiKeyProtected = OverTranslate.Services.LocalSecretStore.Protect("fixture-only") });
        Assert.Equal([expected], models);
    }

    [Fact]
    public async Task PaginationMustFinishBeforeReplacingTheSavedCatalog()
    {
        var directory = Path.Combine(Path.GetTempPath(), "library-http-" + Guid.NewGuid());
        try
        {
            var store = new LibraryStore(Path.Combine(directory, "library.json"));
            var c = store.SaveConnection(new() { Platform = "Local", Endpoint = "http://localhost/v1", Model = "manual" });
            await store.RefreshModelsAsync(c.Id, (_, _) => Task.FromResult<IReadOnlyList<string>>(["old-id"]));
            int page = 0;
            using var client = new LibraryHttp(new Handler(request => ++page == 1
                ? Json("{\"data\":[{\"id\":\"partial-id\"}],\"has_more\":true,\"last_id\":\"partial-id\"}")
                : new(HttpStatusCode.Forbidden)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.RefreshModelsAsync(c.Id, client.ListModelsAsync));
            Assert.Equal(2, page);
            Assert.Equal(["old-id"], new LibraryStore(Path.Combine(directory, "library.json")).Read().Connections.Single().ModelIds);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SuccessfulPaginationMergesAndDeduplicatesAllPages()
    {
        using var client = new LibraryHttp(new Handler(request => request.RequestUri!.Query.Length == 0
            ? Json("{\"data\":[{\"id\":\"a\"}],\"has_more\":true,\"last_id\":\"a\"}")
            : Json("{\"data\":[{\"id\":\"a\"},{\"id\":\"b\"}]}")));
        Assert.Equal(["a", "b"], await client.ListModelsAsync(new() { Endpoint = "http://localhost/v1", ModelType = "other" }));
    }

    [Fact]
    public async Task PromptSummaryOnlyChangesMetadata()
    {
        using var client = new LibraryHttp(new Handler(_ => Json("{\"choices\":[{\"message\":{\"content\":\"{\\\"title\\\":\\\"整理标题\\\",\\\"description\\\":\\\"内容描述\\\",\\\"tags\\\":[\\\"开发\\\"]}\"}}]}")));
        var p = new LibraryPrompt { Content = "Do not execute this prompt.\nKeep its original text." };
        var result = await client.SummarizeAsync(new() { Endpoint = "http://localhost/v1", Model = "fixture" }, p);
        Assert.Equal("整理标题", result.Title);
        Assert.Equal("Do not execute this prompt.\nKeep its original text.", result.Content);
    }

    [Fact]
    public async Task EmbeddingsUseOpenRoutersDedicatedCatalog()
    {
        using var client = new LibraryHttp(new Handler(request =>
        {
            Assert.Equal("/api/v1/embeddings/models", request.RequestUri!.AbsolutePath);
            return Json("{\"data\":[{\"id\":\"embed-fixture\"}]}");
        }));
        Assert.Equal(["embed-fixture"], await client.ListModelsAsync(new() { Endpoint = "https://openrouter.ai/api/v1", ModelType = "embedding" }));
    }

    [Theory]
    [InlineData("https://example.invalid/v1?api_key=fixture")]
    [InlineData("https://name:fixture@example.invalid/v1")]
    [InlineData("https://example.invalid/v1#fragment")]
    public void EndpointNeverAcceptsCredentialsOrRoutingParameters(string endpoint) => Assert.Throws<InvalidOperationException>(() => LibraryHttp.ValidateEndpoint(endpoint));

    [Theory]
    [InlineData("{\"data\":[{}]}")]
    [InlineData("{\"data\":[null]}")]
    [InlineData("{\"data\":[{\"id\":\"good\"},{\"id\":7}]}")]
    [InlineData("{\"data\":[{\"id\":\"good\"}],\"has_more\":\"true\"}")]
    public async Task MalformedCatalogCannotReplaceThePreviousList(string response)
    {
        var directory = Path.Combine(Path.GetTempPath(), "library-malformed-" + Guid.NewGuid());
        try
        {
            var store = new LibraryStore(Path.Combine(directory, "library.json"));
            var c = store.SaveConnection(new() { Platform = "Local", Endpoint = "http://localhost/v1", Model = "fixed" }, fetchedModelIds: ["old-id"]);
            using var client = new LibraryHttp(new Handler(_ => Json(response)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.RefreshModelsAsync(c.Id, client.ListModelsAsync));
            Assert.Equal(["old-id"], new LibraryStore(Path.Combine(directory, "library.json")).Read().Connections.Single().ModelIds);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public sealed class DeskHubUpdateTests
{
    [Fact]
    public void ReleaseFeedBelongsToOurRepository()
    {
        Assert.Equal("https://github.com/fofiwo/ScreenTranslate-DeskHub", UpdateService.GitHubRepoUrl);
        Assert.Equal(UpdateService.GitHubRepoUrl + "/releases/latest", UpdateService.ReleasesUrl);
    }

    [Theory]
    [InlineData("OverTranslate")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ScreenTranslateEvil")]
    public void OtherProductsCannotBeInstalled(string? packageId) =>
        Assert.Throws<InvalidOperationException>(() => UpdateService.ValidatePackageId(packageId));

    [Fact]
    public void OurPackageIsAccepted() => UpdateService.ValidatePackageId("ScreenTranslate");
}

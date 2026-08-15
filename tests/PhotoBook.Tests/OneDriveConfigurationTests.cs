using PhotoBook.Ingestion.OneDrive;

namespace PhotoBook.Tests;

/// <summary>
/// The OneDrive setup gate. Copying the sample <c>onedrive.json</c> without editing it is the most
/// likely setup mistake, and it must produce the actionable "not set up yet" message rather than an
/// opaque MSAL rejection at sign-in — which is exactly what happened before these tests existed.
/// </summary>
public sealed class OneDriveConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PASTE-YOUR-GUID-HERE")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]  // the sample in OneDriveConfiguration.SampleJson
    [InlineData("11111111-2222-3333-4444-555555555555")]  // the sample that used to appear in SETUP.md
    [InlineData("<11111111-2222-3333-4444-555555555555>")]
    public void PlaceholderAndMalformedClientIdsAreNotUsable(string? clientId) =>
        Assert.False(OneDriveConfiguration.IsUsableClientId(clientId));

    [Fact]
    public void ARealGuidIsUsable() =>
        Assert.True(OneDriveConfiguration.IsUsableClientId("6c7e8f21-4b3a-4d5e-9f10-2a3b4c5d6e7f"));

    [Fact]
    public void ConfigurationWithAPlaceholderThrowsTheSetupError()
    {
        var configuration = new OneDriveConfiguration { ClientId = "11111111-2222-3333-4444-555555555555" };

        Assert.False(configuration.IsConfigured);
        var ex = Assert.Throws<OneDriveNotConfiguredException>(configuration.EnsureConfigured);
        Assert.Contains("SETUP.md", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSampleJsonInTheCodeIsItselfRejected()
    {
        // The sample is quoted at the user verbatim, so it must fail the gate it illustrates.
        using var document = System.Text.Json.JsonDocument.Parse(OneDriveConfiguration.SampleJson);
        var clientId = document.RootElement.GetProperty("clientId").GetString();

        Assert.False(OneDriveConfiguration.IsUsableClientId(clientId));
    }

    [Fact]
    public void RedirectUriDefaultsToUnsetSoMsalChoosesBrokerOrLoopback()
    {
        // Pinning a redirect URI breaks whichever path was not pinned; SETUP.md tells the user to
        // leave it out, so the default must agree.
        Assert.Null(new OneDriveConfiguration().RedirectUri);
    }

    [Fact]
    public void DefaultsMatchTheDocumentedSetup()
    {
        var configuration = new OneDriveConfiguration();

        Assert.Equal("https://login.microsoftonline.com/consumers", configuration.Authority);
        Assert.Equal(["User.Read", "Files.Read"], configuration.Scopes);
    }
}

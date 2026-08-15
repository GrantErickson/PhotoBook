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

    // ------------------------------------------------------------------ the app-directory location

    private const string RealClientId = "6c7e8f21-4b3a-4d5e-9f10-2a3b4c5d6e7f";

    /// <summary>
    /// Writes <c>onedrive.json</c> beside the test assembly and removes it again, with the two
    /// environment variables that outrank it cleared for the duration.
    /// <para>Two things keep these tests machine-independent. The test project deliberately does not
    /// set <c>BundleOneDriveConfig</c>, so no real config is ever copied here; and the environment
    /// is neutralised, so a developer who exports a client id for their own runs does not fail the
    /// suite. What cannot be neutralised is <c>%LOCALAPPDATA%\PhotoBook\onedrive.json</c>, which is
    /// a real file on a machine that has been through SETUP.md — so the fall-through tests below
    /// assert only that the rejected file was not adopted, never what was found instead.</para>
    /// </summary>
    private sealed class AppDirectoryConfigFile : IDisposable
    {
        private readonly string? _clientIdVariable;
        private readonly string? _configFileVariable;

        public AppDirectoryConfigFile(string json)
        {
            _clientIdVariable = Environment.GetEnvironmentVariable(OneDriveConfigurationLoader.ClientIdEnvironmentVariable);
            _configFileVariable = Environment.GetEnvironmentVariable(OneDriveConfigurationLoader.ConfigFileEnvironmentVariable);
            Environment.SetEnvironmentVariable(OneDriveConfigurationLoader.ClientIdEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(OneDriveConfigurationLoader.ConfigFileEnvironmentVariable, null);

            Path = OneDriveConfigurationLoader.AppDirectoryConfigurationFilePath;
            Assert.False(File.Exists(Path), $"{Path} already exists; the test project must not bundle a real config.");
            File.WriteAllText(Path, json);
        }

        public string Path { get; }

        public void Dispose()
        {
            File.Delete(Path);
            Environment.SetEnvironmentVariable(OneDriveConfigurationLoader.ClientIdEnvironmentVariable, _clientIdVariable);
            Environment.SetEnvironmentVariable(OneDriveConfigurationLoader.ConfigFileEnvironmentVariable, _configFileVariable);
        }
    }

    [Fact]
    public void AppDirectoryConfigurationSitsBesideTheExecutable()
    {
        // The build copies the repository-root file here; if this ever pointed somewhere else, the
        // "clone and drop one file in" setup path in SETUP.md would silently stop working.
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, OneDriveConfigurationLoader.ConfigFileName),
            OneDriveConfigurationLoader.AppDirectoryConfigurationFilePath);
    }

    [Fact]
    public void AConfigFileBesideTheAppIsFound()
    {
        using var file = new AppDirectoryConfigFile($$"""
            { "schemaVersion": 1, "clientId": "{{RealClientId}}" }
            """);

        var configuration = OneDriveConfigurationLoader.Load();

        Assert.True(configuration.IsConfigured);
        Assert.Equal(RealClientId, configuration.ClientId);
        Assert.Equal(file.Path, configuration.SourceDescription);
        // Omitted fields still fall back to the documented defaults.
        Assert.Equal(OneDriveConfiguration.ConsumersAuthority, configuration.Authority);
        Assert.Equal(["User.Read", "Files.Read"], configuration.Scopes);
    }

    [Fact]
    public void AnExplicitPathBeatsTheFileBesideTheApp()
    {
        const string explicitClientId = "1a2b3c4d-5e6f-4071-8293-a4b5c6d7e8f9";
        using var beside = new AppDirectoryConfigFile($$"""
            { "schemaVersion": 1, "clientId": "{{RealClientId}}" }
            """);
        var explicitPath = Path.Combine(Path.GetTempPath(), $"photobook-onedrive-{Guid.NewGuid():N}.json");
        File.WriteAllText(explicitPath, $$"""
            { "schemaVersion": 1, "clientId": "{{explicitClientId}}" }
            """);

        try
        {
            var configuration = OneDriveConfigurationLoader.Load(explicitPath);

            Assert.Equal(explicitClientId, configuration.ClientId);
        }
        finally
        {
            File.Delete(explicitPath);
        }
    }

    [Fact]
    public void APlaceholderBesideTheAppFallsThroughRatherThanConfiguring()
    {
        // Someone copies the sample into the repo root and forgets to paste their GUID. That must
        // read as "not set up yet" and keep searching, not lock in an unusable client id.
        using var file = new AppDirectoryConfigFile(OneDriveConfiguration.SampleJson);

        var configuration = OneDriveConfigurationLoader.Load();

        Assert.NotEqual(file.Path, configuration.SourceDescription);
        Assert.NotEqual(OneDriveConfiguration.SampleClientId, configuration.ClientId);
    }

    [Fact]
    public void AMalformedFileBesideTheAppIsNotFatal()
    {
        // An unreadable settings file must read as "not set up" and keep searching; the one thing it
        // must never do is take down the app on the way to the OneDrive button.
        using var file = new AppDirectoryConfigFile("{ this is not json");

        var configuration = OneDriveConfigurationLoader.Load();

        Assert.NotEqual(file.Path, configuration.SourceDescription);
    }
}

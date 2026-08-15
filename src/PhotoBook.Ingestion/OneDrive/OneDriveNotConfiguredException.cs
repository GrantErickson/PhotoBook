using System.Text;

namespace PhotoBook.Ingestion.OneDrive;

/// <summary>
/// Thrown when a OneDrive operation is attempted before an Entra app registration client id exists
/// (ADR-0011 "Consequences": the app registration is the user's own). The message is the whole
/// feature here — it names the file to create, the value to put in it, and the setup document —
/// because "OneDrive doesn't work" with no explanation is the failure mode this class exists to
/// prevent. The local-folder path (R1) keeps working regardless.
/// </summary>
public sealed class OneDriveNotConfiguredException : InvalidOperationException
{
    /// <summary>The setup document this exception points the user at.</summary>
    public const string SetupDocument = "SETUP.md";

    /// <summary>Creates the exception from the configuration that came back unconfigured.</summary>
    /// <param name="configuration">The configuration that was loaded, including where it came from.</param>
    public OneDriveNotConfiguredException(OneDriveConfiguration configuration)
        : base(BuildMessage(configuration))
    {
        Configuration = configuration;
        ConfigurationFilePath = OneDriveConfigurationLoader.DefaultConfigurationFilePath;
    }

    /// <summary>The configuration that was found (or the empty default).</summary>
    public OneDriveConfiguration Configuration { get; }

    /// <summary>The file the user should create.</summary>
    public string ConfigurationFilePath { get; }

    private static string BuildMessage(OneDriveConfiguration configuration)
    {
        var message = new StringBuilder();
        message.AppendLine("OneDrive sync is not set up yet, so PhotoBook cannot sign in to your Microsoft account.");
        message.AppendLine();
        message.AppendLine("OneDrive needs an Entra app registration in your own Microsoft account — a free,");
        message.AppendLine($"one-time step. Follow \"Set up OneDrive\" in {SetupDocument}, then put the");
        message.AppendLine("application (client) id it gives you in either of these places:");
        message.AppendLine();
        message.AppendLine($"  1. The environment variable {OneDriveConfigurationLoader.ClientIdEnvironmentVariable};");
        message.AppendLine($"  2. onedrive.json in the repository root, which the build copies to");
        message.AppendLine($"     {OneDriveConfigurationLoader.AppDirectoryConfigurationFilePath}; or");
        message.AppendLine($"  3. The file {OneDriveConfigurationLoader.DefaultConfigurationFilePath}:");
        message.AppendLine();
        foreach (var line in OneDriveConfiguration.SampleJson.Split('\n'))
            message.AppendLine("     " + line.TrimEnd('\r'));
        message.AppendLine();
        message.AppendLine("The registration must support personal Microsoft accounts, request the delegated");
        message.AppendLine("scopes User.Read and Files.Read (read-only — PhotoBook never writes to OneDrive),");
        message.AppendLine("and carry both redirect URIs under \"Mobile and desktop applications\":");
        message.AppendLine();
        message.AppendLine("     ms-appx-web://microsoft.aad.brokerplugin/<your-client-id>   (Windows sign-in)");
        message.AppendLine("     http://localhost                                            (browser fallback)");
        message.AppendLine();

        if (!string.IsNullOrWhiteSpace(configuration.SourceDescription))
            message.AppendLine($"Checked: {configuration.SourceDescription}.");

        message.Append("Until then, use \"New Book → Local folder\" — the local-folder path works offline and needs no setup.");
        return message.ToString();
    }
}

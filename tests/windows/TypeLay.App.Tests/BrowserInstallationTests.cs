using System.IO;
using System.Text.Json;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class BrowserInstallationTests
{
    [Fact]
    public void LoadedExtensionIsDiscoveredFromItsExactShippedDirectoryWithoutChangingPreferences()
    {
        string root = Path.Combine(Path.GetTempPath(), "typelay-browser-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            string extension = Path.Combine(root, "发行 package", "browser", "chromium");
            Directory.CreateDirectory(extension);
            File.WriteAllText(Path.Combine(extension, "manifest.json"), "{}");
            string profile = Path.Combine(root, "User Data", "Default");
            Directory.CreateDirectory(profile);
            const string identity = "abcdefghijklmnopabcdefghijklmnop";
            string preferences = Path.Combine(profile, "Secure Preferences");
            byte[] original = JsonSerializer.SerializeToUtf8Bytes(new
            {
                extensions = new
                {
                    settings = new Dictionary<string, object> { [identity] = new { path = extension } }
                }
            });
            File.WriteAllBytes(preferences, original);
            Assert.Equal(identity, BrowserExtensionInstallation.FindUnique(extension, Path.GetDirectoryName(profile)!));
            Assert.Equal(original, File.ReadAllBytes(preferences));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Theory]
    [InlineData("other-directory")]
    [InlineData("relative-directory")]
    [InlineData("invalid-identity")]
    [InlineData("ambiguous-identity")]
    [InlineData("malformed-preferences")]
    [InlineData("missing-manifest")]
    public void UnprovenOrAmbiguousInstallationCannotSupplyAConnectionIdentity(string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), "typelay-browser-refuse-" + Guid.NewGuid().ToString("N"));
        try
        {
            string extension = Path.Combine(root, "package", "browser", "chromium");
            Directory.CreateDirectory(extension);
            if (scenario != "missing-manifest")
                File.WriteAllText(Path.Combine(extension, "manifest.json"), "{}");
            string profileRoot = Path.Combine(root, "User Data"), profile = Path.Combine(profileRoot, "Default");
            Directory.CreateDirectory(profile);
            string path = scenario == "other-directory" ? extension + "-old"
                : scenario == "relative-directory" ? "package/browser/chromium" : extension;
            string identity = new(scenario == "invalid-identity" ? 'q' : 'a', 32);
            File.WriteAllText(Path.Combine(profile, "Preferences"), JsonSerializer.Serialize(new
            {
                extensions = new
                {
                    settings = new Dictionary<string, object> { [identity] = new { path } }
                }
            }));
            if (scenario == "ambiguous-identity")
            {
                string second = Path.Combine(profileRoot, "Profile 1");
                Directory.CreateDirectory(second);
                File.WriteAllText(Path.Combine(second, "Secure Preferences"), JsonSerializer.Serialize(new
                {
                    extensions = new
                    {
                        settings = new Dictionary<string, object> { [new string('b', 32)] = new { path = extension } }
                    }
                }));
            }
            if (scenario == "malformed-preferences")
                File.WriteAllText(Path.Combine(profile, "Secure Preferences"), "{");
            Assert.Null(BrowserExtensionInstallation.FindUnique(extension, profileRoot));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

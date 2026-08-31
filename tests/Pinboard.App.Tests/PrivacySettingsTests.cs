using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Models;
using Pinboard.App.Services;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class PrivacySettingsTests
{
    [TestMethod]
    public void NewInstall_UsesDocumentsLibraryAndTextCollectionIsOptIn()
    {
        var settings = new AppSettings();

        Assert.AreEqual(
            Path.GetFullPath(AppSettings.DefaultLibraryPath),
            Path.GetFullPath(settings.LibraryPath));
        Assert.IsFalse(settings.TextCaptureEnabled);
        Assert.IsTrue(settings.TextPrivacyModeEnabled);
        Assert.IsTrue(settings.TextCaptureExcludedApplications.Contains("Bitwarden"));
    }

    [TestMethod]
    public void ExistingExplicitTextCollectionChoice_RoundTripsUnchanged()
    {
        var enabled = new AppSettings { TextCaptureEnabled = true };
        var disabled = new AppSettings { TextCaptureEnabled = false };

        Assert.IsTrue(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(enabled))!.TextCaptureEnabled);
        Assert.IsFalse(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(disabled))!.TextCaptureEnabled);
    }

    [DataTestMethod]
    [DataRow("password = correct-horse-battery-staple")]
    [DataRow("Bearer abcdefghijklmnopqrstuvwxyz")]
    [DataRow("123456")]
    [DataRow("4111 1111 1111 1111")]
    [DataRow("-----BEGIN " + "PRIVATE KEY-----\nprivate-material")]
    public void PrivacyFilter_BlocksCommonSensitiveShapes(string value)
    {
        var filter = new ClipboardTextPrivacyFilter();

        Assert.IsTrue(filter.LooksSensitive(value));
    }

    [DataTestMethod]
    [DataRow("A normal research note about password policies")]
    [DataRow("Meeting at 14:30 tomorrow")]
    [DataRow("123")]
    public void PrivacyFilter_DoesNotBlockOrdinaryNotes(string value)
    {
        var filter = new ClipboardTextPrivacyFilter();

        Assert.IsFalse(filter.LooksSensitive(value));
    }
}

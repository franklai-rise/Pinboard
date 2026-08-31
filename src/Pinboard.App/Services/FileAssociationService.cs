using Microsoft.Win32;

namespace Pinboard.App.Services;

public sealed class FileAssociationService
{
    private const string ClassName = "Pinboard.Document";

    public void Register()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException(LocalizationService.T("ExecutablePathUnknown"));
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        using (var extension = classes.CreateSubKey(".pinboard"))
        {
            extension.SetValue(string.Empty, ClassName);
        }
        using (var document = classes.CreateSubKey(ClassName))
        {
            document.SetValue(string.Empty, LocalizationService.T("FileAssociationDescription"));
            document.CreateSubKey("DefaultIcon")?.SetValue(string.Empty, $"\"{exe}\",0");
            document.CreateSubKey(@"shell\open\command")?.SetValue(string.Empty, $"\"{exe}\" --open \"%1\"");
        }
        using (var protocol = classes.CreateSubKey("pinboard"))
        {
            protocol.SetValue(string.Empty, "URL:Pinboard Protocol");
            protocol.SetValue("URL Protocol", string.Empty);
            protocol.CreateSubKey(@"shell\open\command")?.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
        }
    }
}

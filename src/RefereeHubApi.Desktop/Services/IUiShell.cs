namespace RefereeHubApi.Desktop.Services;

// window-level services view models need: clipboard, files, browser.
public interface IUiShell
{
    Task CopyAsync(string text);

    // returns the chosen path, or null when cancelled.
    Task<string?> SaveTextAsync(string suggestedName, string content);

    Task OpenUrlAsync(Uri uri);
}

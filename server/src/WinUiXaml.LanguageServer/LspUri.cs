namespace WinUiXaml.LanguageServer;

/// <summary>Converts LSP document URIs to paths on the server's host OS.</summary> <remarks>Handles percent-encoded drive colons that <see cref="System.Uri.LocalPath"/> misinterprets.</remarks>
internal static class LspUri
{
    public static string? ToPath(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        // Non-file URI schemes have no local path.
        if (!uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return IsWindowsPath(uri) ? uri : null;
        }

        Uri parsed;
        try
        {
            parsed = new Uri(uri);
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (!parsed.IsFile)
        {
            return null;
        }

        var absolutePath = Uri.UnescapeDataString(parsed.AbsolutePath);

        // The server is also deployed as a managed DLL on macOS and Linux. A POSIX file URI is
        // not a Windows URI merely because WinUI is in the product name: replacing its slashes
        // turns file:///Users/me/project into a path below the server's working directory, which
        // silently fails the trusted-workspace check before project evaluation begins.
        var driveOffset = absolutePath.Length > 0 && absolutePath[0] == '/' ? 1 : 0;
        var isDrivePath = absolutePath.Length >= driveOffset + 2 &&
            char.IsLetter(absolutePath[driveOffset]) && absolutePath[driveOffset + 1] == ':';
        if (!OperatingSystem.IsWindows() && string.IsNullOrEmpty(parsed.Host) && !isDrivePath)
        {
            return absolutePath;
        }

        var path = absolutePath.Replace('/', '\\');

        if (!string.IsNullOrEmpty(parsed.Host))
        {
            // UNC: file://server/share/... -> \\server\share\...
            return $"\\\\{parsed.Host}{path}";
        }

        // Drop the spurious leading slash before a "<drive>:" segment ("\c:\Users" -> "c:\Users").
        if (path.Length >= 3 && path[0] == '\\' && char.IsLetter(path[1]) && path[2] == ':')
        {
            path = path[1..];
        }

        return path;
    }

    public static string FromPath(string path) => new Uri(path).AbsoluteUri;

    private static bool IsWindowsPath(string s) =>
        (s.Length >= 2 && char.IsLetter(s[0]) && s[1] == ':') ||
        s.StartsWith(@"\\", StringComparison.Ordinal);
}

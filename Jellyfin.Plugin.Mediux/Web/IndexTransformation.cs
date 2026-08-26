namespace Jellyfin.Plugin.Mediux.Web;

/// <summary>
/// FileTransformation callback that injects the set browser script into index.html.
/// </summary>
public static class IndexTransformation
{
    private const string ScriptTag = "<script src=\"/MediUX/SetBrowser.js\" defer></script>";

    /// <summary>
    /// Called by FileTransformation with the current index.html contents.
    /// </summary>
    /// <param name="content">The patch request payload.</param>
    /// <returns>The transformed file contents.</returns>
    public static string Transform(PatchRequestPayload content)
    {
        if (string.IsNullOrEmpty(content.Contents))
        {
            return content.Contents ?? string.Empty;
        }

        // FileTransformation treats fileNamePattern as regex; "index.html" also matches
        // webpack chunks like *-index-html.*.js — skip anything that is not HTML.
        if (!IsHtmlDocument(content.Contents))
        {
            return content.Contents;
        }

        if (content.Contents.Contains("SetBrowser.js", StringComparison.Ordinal))
        {
            return content.Contents;
        }

        var headClose = content.Contents.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headClose >= 0)
        {
            return content.Contents[..headClose] + ScriptTag + "\n" + content.Contents[headClose..];
        }

        var bodyClose = content.Contents.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyClose >= 0)
        {
            return content.Contents[..bodyClose] + ScriptTag + "\n" + content.Contents[bodyClose..];
        }

        return content.Contents;
    }

    private static bool IsHtmlDocument(string contents)
    {
        var trimmed = contents.AsSpan().TrimStart();
        return trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || contents.Contains("</head>", StringComparison.OrdinalIgnoreCase)
            || contents.Contains("</body>", StringComparison.OrdinalIgnoreCase);
    }
}

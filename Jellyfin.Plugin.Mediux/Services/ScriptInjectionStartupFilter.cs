using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediux.Services;

/// <summary>
/// Injects the MediUX set-browser script into jellyfin-web's index.html at request time
/// via ASP.NET middleware registered through <see cref="IStartupFilter"/>.
/// </summary>
/// <remarks>
/// Avoids writing to the web folder and does not require the File Transformation plugin.
/// Works on Jellyfin 10.10+, 10.11, and 12.
/// </remarks>
public sealed class ScriptInjectionStartupFilter : IStartupFilter
{
    private const string ScriptMarker = "SetBrowser.js";
    private const string ScriptTag = "<script src=\"/MediUX/SetBrowser.js\" defer></script>";

    private readonly ILogger<ScriptInjectionStartupFilter> _logger;
    private int _loggedOnce;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptInjectionStartupFilter"/> class.
    /// </summary>
    public ScriptInjectionStartupFilter(ILogger<ScriptInjectionStartupFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Registered before the rest of the pipeline so this runs outermost —
            // stripping Accept-Encoding then reliably yields an uncompressed response.
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private async Task InvokeAsync(HttpContext context, Func<Task> nextMw)
    {
        if (!IsIndexRequest(context.Request.Path.Value))
        {
            await nextMw().ConfigureAwait(false);
            return;
        }

        // Only GET produces a body we can rewrite.
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await nextMw().ConfigureAwait(false);
            return;
        }

        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await nextMw().ConfigureAwait(false);
        }
        catch
        {
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;
        buffer.Seek(0, SeekOrigin.Begin);

        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);

        if (!isHtml)
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        string html;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        try
        {
            var alreadyInjected = html.IndexOf(ScriptMarker, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!alreadyInjected)
            {
                var headClose = html.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                if (headClose >= 0)
                {
                    html = html[..headClose] + ScriptTag + "\n" + html[headClose..];
                }
                else
                {
                    var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    if (bodyClose >= 0)
                    {
                        html = html[..bodyClose] + ScriptTag + "\n" + html[bodyClose..];
                    }
                }

                if (html.IndexOf(ScriptMarker, StringComparison.OrdinalIgnoreCase) >= 0
                    && Interlocked.Exchange(ref _loggedOnce, 1) == 0)
                {
                    _logger.LogInformation(
                        "MediUX: injected SetBrowser.js via request-time middleware (IStartupFilter).");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MediUX: script injection middleware error (serving original HTML)");
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html;charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }

    /// <summary>
    /// Matches the web app shell: /web, /web/, /web/index.html (including base-url prefixes).
    /// </summary>
    private static bool IsIndexRequest(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/web", StringComparison.OrdinalIgnoreCase);
    }
}

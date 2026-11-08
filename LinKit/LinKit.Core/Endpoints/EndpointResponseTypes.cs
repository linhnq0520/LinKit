using System;
using System.IO;
using System.Text;

namespace LinKit.Core.Endpoints;

/// <summary>
/// Clean-Architecture POCO response for returning binary content, documents, images and streams without taking a
/// dependency on <c>Microsoft.AspNetCore.Http.IResult</c>. Exactly one of <see cref="Stream"/>, <see cref="Bytes"/>
/// or <see cref="FilePath"/> must be set; the generated endpoint layer picks the matching ASP.NET Core result.
/// </summary>
public record FileResponse
{
    /// <summary>
    /// Gets the content stream. Used when the payload is produced on the fly and should not be buffered in memory.
    /// </summary>
    public Stream? Stream { get; init; }

    /// <summary>
    /// Gets the payload as a byte array, when it is already materialised.
    /// </summary>
    public byte[]? Bytes { get; init; }

    /// <summary>
    /// Gets the path of a file on disk to serve with <c>Results.File</c>; the file is streamed by the server.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Gets the MIME type sent in the <c>Content-Type</c> header. Defaults to <c>application/octet-stream</c>.
    /// </summary>
    public string ContentType { get; init; } = "application/octet-stream";

    /// <summary>
    /// Gets the value of the <c>Content-Disposition</c> file name; when set, browsers offer a download instead of
    /// rendering the content inline.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// Gets a value indicating whether HTTP range requests are honoured, which is required for resumable downloads and
    /// media seeking.
    /// </summary>
    public bool EnableRangeProcessing { get; init; }

    /// <summary>
    /// Gets the last modification time, enabling conditional requests and <c>304 Not Modified</c> responses.
    /// </summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>
    /// Creates a response backed by a byte array.
    /// </summary>
    /// <param name="bytes">The payload.</param>
    /// <param name="contentType">MIME type of the payload.</param>
    /// <param name="fileName">File name offered to the client as a download.</param>
    /// <param name="enableRangeProcessing">Whether range requests are supported.</param>
    /// <param name="lastModified">Last modification time for conditional requests.</param>
    /// <returns>A <see cref="FileResponse"/> over <paramref name="bytes"/>.</returns>
    public static FileResponse FromBytes(
        byte[] bytes,
        string contentType = "application/octet-stream",
        string? fileName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null
    )
    {
        return new FileResponse
        {
            Bytes = bytes,
            ContentType = contentType,
            FileName = fileName,
            EnableRangeProcessing = enableRangeProcessing,
            LastModified = lastModified,
        };
    }

    /// <summary>
    /// Creates a response backed by a stream. The stream is disposed by the server after it has been sent.
    /// </summary>
    /// <param name="stream">The content stream.</param>
    /// <param name="contentType">MIME type of the payload.</param>
    /// <param name="fileName">File name offered to the client as a download.</param>
    /// <param name="enableRangeProcessing">Whether range requests are supported.</param>
    /// <param name="lastModified">Last modification time for conditional requests.</param>
    /// <returns>A <see cref="FileResponse"/> over <paramref name="stream"/>.</returns>
    public static FileResponse FromStream(
        Stream stream,
        string contentType = "application/octet-stream",
        string? fileName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null
    )
    {
        return new FileResponse
        {
            Stream = stream,
            ContentType = contentType,
            FileName = fileName,
            EnableRangeProcessing = enableRangeProcessing,
            LastModified = lastModified,
        };
    }

    /// <summary>
    /// Creates a response backed by a file on disk, which the server streams without loading it into memory.
    /// </summary>
    /// <param name="filePath">Physical path of the file to serve.</param>
    /// <param name="contentType">MIME type of the payload.</param>
    /// <param name="fileName">File name offered to the client as a download.</param>
    /// <param name="enableRangeProcessing">Whether range requests are supported.</param>
    /// <param name="lastModified">Last modification time for conditional requests.</param>
    /// <returns>A <see cref="FileResponse"/> over <paramref name="filePath"/>.</returns>
    public static FileResponse FromFile(
        string filePath,
        string contentType = "application/octet-stream",
        string? fileName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null
    )
    {
        return new FileResponse
        {
            FilePath = filePath,
            ContentType = contentType,
            FileName = fileName,
            EnableRangeProcessing = enableRangeProcessing,
            LastModified = lastModified,
        };
    }
}

/// <summary>
/// Clean-Architecture POCO response for HTTP redirects, for example OAuth callbacks or payment gateway returns.
/// </summary>
/// <param name="Url">Absolute or relative target of the redirect.</param>
/// <param name="Permanent">Whether the redirect uses 301; <see langword="false"/> uses 302.</param>
/// <param name="PreserveMethod">Whether the client must repeat the request with the same method and body (308).</param>
public record RedirectResponse(string Url, bool Permanent = false, bool PreserveMethod = false)
{
    /// <summary>
    /// Creates a redirect response.
    /// </summary>
    /// <param name="url">Target of the redirect.</param>
    /// <param name="permanent">Whether the redirect uses 301; <see langword="false"/> uses 302.</param>
    /// <param name="preserveMethod">Whether the client must repeat the request with the same method and body (308).</param>
    /// <returns>A <see cref="RedirectResponse"/> pointing at <paramref name="url"/>.</returns>
    public static RedirectResponse To(
        string url,
        bool permanent = false,
        bool preserveMethod = false
    ) => new(url, permanent, preserveMethod);
}

/// <summary>
/// Clean-Architecture POCO response for raw text, HTML or XML output, for example webhooks, SVG images or HTML
/// emails rendered by a handler.
/// </summary>
/// <param name="Content">The body to send to the client.</param>
/// <param name="ContentType">MIME type of the body.</param>
/// <param name="StatusCode">Optional status code overriding the default 200.</param>
/// <param name="Encoding">Optional encoding used to serialize <paramref name="Content"/>.</param>
public record ContentResponse(
    string Content,
    string ContentType = "text/plain",
    int? StatusCode = null,
    Encoding? Encoding = null
)
{
    /// <summary>
    /// Creates an HTML response with a UTF-8 content type.
    /// </summary>
    /// <param name="html">The HTML body.</param>
    /// <param name="statusCode">Optional status code overriding the default 200.</param>
    /// <returns>A <see cref="ContentResponse"/> carrying HTML.</returns>
    public static ContentResponse Html(string html, int? statusCode = null) =>
        new(html, "text/html; charset=utf-8", statusCode);

    /// <summary>
    /// Creates an XML response with a UTF-8 content type.
    /// </summary>
    /// <param name="xml">The XML body.</param>
    /// <param name="statusCode">Optional status code overriding the default 200.</param>
    /// <returns>A <see cref="ContentResponse"/> carrying XML.</returns>
    public static ContentResponse Xml(string xml, int? statusCode = null) =>
        new(xml, "application/xml; charset=utf-8", statusCode);

    /// <summary>
    /// Creates a plain text response with a UTF-8 content type.
    /// </summary>
    /// <param name="text">The text body.</param>
    /// <param name="statusCode">Optional status code overriding the default 200.</param>
    /// <returns>A <see cref="ContentResponse"/> carrying plain text.</returns>
    public static ContentResponse Text(string text, int? statusCode = null) =>
        new(text, "text/plain; charset=utf-8", statusCode);
}

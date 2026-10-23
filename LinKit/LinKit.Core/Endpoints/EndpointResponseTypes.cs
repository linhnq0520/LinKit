using System;
using System.IO;
using System.Text;

namespace LinKit.Core.Endpoints;

/// <summary>
/// Clean-Architecture POCO response for returning binary files, documents, images, and streams
/// without taking dependencies on Microsoft.AspNetCore.Http.IResult.
/// </summary>
public record FileResponse
{
    public Stream? Stream { get; init; }
    public byte[]? Bytes { get; init; }
    public string? FilePath { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public string? FileName { get; init; }
    public bool EnableRangeProcessing { get; init; }
    public DateTimeOffset? LastModified { get; init; }

    public static FileResponse FromBytes(
        byte[] bytes,
        string contentType = "application/octet-stream",
        string? fileName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null)
    {
        return new FileResponse
        {
            Bytes = bytes,
            ContentType = contentType,
            FileName = fileName,
            EnableRangeProcessing = enableRangeProcessing,
            LastModified = lastModified
        };
    }

    public static FileResponse FromStream(
        Stream stream,
        string contentType = "application/octet-stream",
        string? fileName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null)
    {
        return new FileResponse
        {
            Stream = stream,
            ContentType = contentType,
            FileName = fileName,
            EnableRangeProcessing = enableRangeProcessing,
            LastModified = lastModified
        };
    }

    public static FileResponse FromFile(
        string filePath,
        string contentType = "application/octet-stream",
        string? fileName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null)
    {
        return new FileResponse
        {
            FilePath = filePath,
            ContentType = contentType,
            FileName = fileName,
            EnableRangeProcessing = enableRangeProcessing,
            LastModified = lastModified
        };
    }
}

/// <summary>
/// Clean-Architecture POCO response for HTTP redirects (e.g. OAuth callbacks, payment gateways).
/// </summary>
public record RedirectResponse(string Url, bool Permanent = false, bool PreserveMethod = false)
{
    public static RedirectResponse To(string url, bool permanent = false, bool preserveMethod = false)
        => new(url, permanent, preserveMethod);
}

/// <summary>
/// Clean-Architecture POCO response for raw text, HTML, or XML output (e.g. webhooks, SVG, emails).
/// </summary>
public record ContentResponse(string Content, string ContentType = "text/plain", int? StatusCode = null, Encoding? Encoding = null)
{
    public static ContentResponse Html(string html, int? statusCode = null)
        => new(html, "text/html; charset=utf-8", statusCode);

    public static ContentResponse Xml(string xml, int? statusCode = null)
        => new(xml, "application/xml; charset=utf-8", statusCode);

    public static ContentResponse Text(string text, int? statusCode = null)
        => new(text, "text/plain; charset=utf-8", statusCode);
}

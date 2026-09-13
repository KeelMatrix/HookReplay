using System.Net.Http.Headers;
using System.Text;

namespace KeelMatrix.HookReplay;

/// <summary>
/// Resolves the text encoding used for captured and replayed bodies.
/// </summary>
/// <remarks>
/// A declared <c>charset</c> is honored when it names one of the bounded supported
/// encodings; undeclared text is UTF-8. Unsupported declared charsets fail closed
/// instead of being silently reinterpreted.
/// </remarks>
internal static class DeclaredTextEncoding
{
    private static readonly Encoding Default =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static Encoding Resolve(string? charset)
    {
        if (charset is null || charset.Trim().Length == 0)
            return Default;

        string name = charset.Trim().Trim('"').ToLowerInvariant();
        switch (name)
        {
            case "":
            case "utf-8":
            case "utf8":
                return Default;
            case "us-ascii":
            case "ascii":
                return FromCodePage(20127, charset);
            case "iso-8859-1":
            case "iso8859-1":
            case "latin1":
            case "latin-1":
                return FromCodePage(28591, charset);
            case "utf-16":
            case "utf-16le":
            case "utf16":
            case "unicode":
                return new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
            case "utf-16be":
            case "utf16be":
                return new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
            case "utf-32":
            case "utf-32le":
            case "utf32":
                return new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true);
            case "utf-32be":
            case "utf32be":
                return new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true);
            default:
                throw new HookReplayUnsupportedContentException(
                    "The declared charset '" + charset + "' is not supported. HookReplay supports UTF-8 (the default), " +
                    "US-ASCII, ISO-8859-1, UTF-16, and UTF-32 text content.");
        }
    }

    public static string? GetCharset(string? contentType)
    {
        if (contentType is null || contentType.Trim().Length == 0)
            return null;

        return MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? parsed) &&
            parsed is not null
            ? parsed.CharSet
            : null;
    }

    public static string Decode(Encoding encoding, byte[] bytes)
    {
        try
        {
            return encoding.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new HookReplayUnsupportedContentException(
                "The HTTP content is not valid " + encoding.WebName + " text.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new HookReplayUnsupportedContentException(
                "The HTTP content is not valid " + encoding.WebName + " text.", exception);
        }
    }

    public static void EnsureEncodable(Encoding encoding, string text, string subject)
    {
        try
        {
            _ = encoding.GetBytes(text);
        }
        catch (EncoderFallbackException exception)
        {
            throw new HookReplayUnsupportedContentException(
                subject + " cannot be represented as " + encoding.WebName +
                " text. Declare UTF-8 or a charset that can represent the recorded content.",
                exception);
        }
    }

    private static Encoding FromCodePage(int codePage, string charset)
    {
        try
        {
            return Encoding.GetEncoding(
                codePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }
        catch (Exception exception)
        {
            throw new HookReplayUnsupportedContentException(
                "The declared charset '" + charset + "' is not supported on this platform.",
                exception);
        }
    }
}

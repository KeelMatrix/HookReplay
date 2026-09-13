using System.Net;

namespace KeelMatrix.HookReplay;

internal sealed class CassetteHeader
{
    public CassetteHeader(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public string Value { get; }
}

internal sealed class CassetteInteraction
{
    public CassetteRequest Request { get; set; } = null!;
    public CassetteResponse Response { get; set; } = null!;
    public bool Consumed { get; set; }
}

internal sealed class CassetteRequest
{
    public string Method { get; set; } = string.Empty;
    public string NormalizedUri { get; set; } = string.Empty;
    public string? BodyFingerprint { get; set; }
    public string? Body { get; set; }
    public string? BodyContentType { get; set; }
    public List<CassetteHeader> Headers { get; set; } = new();
    public List<CassetteHeader> MatchHeaders { get; set; } = new();
}

internal sealed class CassetteResponse
{
    public int StatusCode { get; set; }
    public string? ReasonPhrase { get; set; }
    public Version Version { get; set; } = HttpVersion.Version11;
    public List<CassetteHeader> Headers { get; set; } = new();
    public List<CassetteHeader> BodyHeaders { get; set; } = new();
    public CassetteBody? Body { get; set; }
}

internal sealed class CassetteBody
{
    /// <summary>
    /// The declared content type, or <see langword="null"/> when the captured content had none.
    /// </summary>
    /// <remarks>
    /// A null value means "no content type was declared", and replay must not synthesize one.
    /// </remarks>
    public string? ContentType { get; set; }

    public string Text { get; set; } = string.Empty;
}

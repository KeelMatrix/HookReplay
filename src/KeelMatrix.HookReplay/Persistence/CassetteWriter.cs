using System.Text.Encodings.Web;
using System.Text.Json;

namespace KeelMatrix.HookReplay;

internal static class CassetteWriter
{
    internal static byte[] Serialize(
        int schemaVersion,
        IReadOnlyList<CassetteInteraction> interactions)
    {
        using var stream = new LimitedMemoryStream(
            (int)CassetteFileSystem.MaxCassetteBytes,
            CassetteFileSystem.MaxCassetteSizeMessage);
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", schemaVersion);
            writer.WritePropertyName("interactions");
            writer.WriteStartArray();
            foreach (CassetteInteraction interaction in interactions)
            {
                writer.WriteStartObject();
                WriteRequest(writer, interaction.Request);
                WriteResponse(writer, interaction.Response);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void WriteRequest(Utf8JsonWriter writer, CassetteRequest request)
    {
        writer.WritePropertyName("request");
        writer.WriteStartObject();
        writer.WriteString("method", request.Method);
        writer.WriteString("normalizedUri", request.NormalizedUri);
        writer.WriteString("bodyFingerprint", request.BodyFingerprint);
        writer.WriteString("bodyContentType", request.BodyContentType);
        writer.WriteString("body", request.Body);
        WriteHeaders(writer, "headers", request.Headers);
        WriteHeaders(writer, "matchHeaders", request.MatchHeaders);
        writer.WriteEndObject();
    }

    private static void WriteResponse(Utf8JsonWriter writer, CassetteResponse response)
    {
        writer.WritePropertyName("response");
        writer.WriteStartObject();
        writer.WriteNumber("statusCode", response.StatusCode);
        writer.WriteString("reasonPhrase", response.ReasonPhrase);
        writer.WriteString("version", response.Version.ToString());
        WriteHeaders(writer, "headers", response.Headers);
        WriteHeaders(writer, "bodyHeaders", response.BodyHeaders);
        writer.WritePropertyName("body");
        if (response.Body is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("contentType", response.Body.ContentType);
            writer.WriteString("text", response.Body.Text);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteHeaders(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<CassetteHeader> headers)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (CassetteHeader header in headers
            .OrderBy(header => header.Name, StringComparer.Ordinal)
            .ThenBy(header => header.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", header.Name);
            writer.WriteString("value", header.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

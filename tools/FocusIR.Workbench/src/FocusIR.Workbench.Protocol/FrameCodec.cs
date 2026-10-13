using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace FocusIR.Workbench.Protocol;

public static class FrameCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = ProtocolLimits.MaximumJsonDepth
    };

    public static byte[] Encode(ReadOnlySpan<byte> json)
    {
        ValidateJson(json);
        if (json.Length is < 1 or > ProtocolLimits.MaximumMessageBytes)
            throw new ProtocolException($"Message size must be between 1 and {ProtocolLimits.MaximumMessageBytes} bytes.");

        var frame = new byte[sizeof(uint) + json.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, sizeof(uint)), (uint)json.Length);
        json.CopyTo(frame.AsSpan(sizeof(uint)));
        return frame;
    }

    public static byte[] Encode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(json);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ProtocolException("Message is not valid UTF-8.", ex);
        }

        return Encode(bytes);
    }

    public static async ValueTask<JsonDocument> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(uint)];
        await ReadExactlyWithTimeoutAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is < 1 or > ProtocolLimits.MaximumMessageBytes)
            throw new ProtocolException($"Invalid frame length: {length}.");

        var body = new byte[(int)length];
        await ReadExactlyWithTimeoutAsync(stream, body, cancellationToken).ConfigureAwait(false);
        ValidateJson(body);
        try
        {
            return JsonDocument.Parse(body, DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("Frame body is not a valid v1 JSON object.", ex);
        }
    }

    public static async ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (frame.Length is < sizeof(uint) + 1 or > sizeof(uint) + ProtocolLimits.MaximumMessageBytes)
            throw new ProtocolException("Encoded frame length is outside the v1 limit.");

        var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(frame.Span[..sizeof(uint)]);
        if (declaredLength != frame.Length - sizeof(uint))
            throw new ProtocolException("Encoded frame header does not match its body length.");

        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ReadExactlyWithTimeoutAsync(Stream stream, Memory<byte> destination, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProtocolLimits.FrameReadTimeout);
        var offset = 0;
        try
        {
            while (offset < destination.Length)
            {
                var read = await stream.ReadAsync(destination[offset..], timeout.Token).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("Peer closed the connection in the middle of a frame.");
                offset += read;
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProtocolException("Timed out while receiving a frame.", ex);
        }
    }

    private static void ValidateJson(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0)
            throw new ProtocolException("Empty JSON frame.");

        try
        {
            _ = StrictUtf8.GetCharCount(body);
            using var document = JsonDocument.Parse(body.ToArray(), DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ProtocolException("Top-level v1 JSON value must be an object.");
            RejectDuplicateProperties(document.RootElement);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ProtocolException("Frame body contains invalid UTF-8.", ex);
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("Frame body contains invalid JSON or exceeds the nesting limit.", ex);
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ProtocolException($"Duplicate JSON property '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }
}

namespace FocusIR.Workbench.Protocol;

public static class ProtocolLimits
{
    public const int Version = 1;
    public const string WorkbenchTag = "FOCUSIR_FACTORY_WORKBENCH_V1";
    public const int MaximumMessageBytes = 256 * 1024;
    public const int MaximumJsonDepth = 16;
    public const int MaximumQueuedBytes = 1024 * 1024;
    public static readonly TimeSpan FrameReadTimeout = TimeSpan.FromSeconds(5);
}

public sealed class ProtocolException(string message, Exception? innerException = null)
    : IOException(message, innerException);

public sealed class RequestTimeoutException(Guid requestId, string method)
    : TimeoutException($"Request '{method}' timed out. Its execution result is unknown; query request.get_status before deciding what to do.")
{
    public Guid RequestId { get; } = requestId;
    public string Method { get; } = method;
}

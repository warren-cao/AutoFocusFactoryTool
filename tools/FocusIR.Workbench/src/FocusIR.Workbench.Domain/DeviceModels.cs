namespace FocusIR.Workbench.Domain;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Faulted
}

public enum DataSourceKind
{
    Unknown,
    Device,
    Simulator
}

public sealed record ConnectionSettings(
    string Host,
    int Port,
    string WorkbenchTag,
    Guid ClientId,
    string ClientName,
    string ClientVersion);

public sealed record DeviceSummary(
    string SerialNumber,
    string LensModel,
    string FirmwareVersion,
    string ServiceVersion,
    string Role,
    string BootId,
    string SnapshotSequence,
    DataSourceKind DataSource,
    IReadOnlySet<string> SupportedMethods,
    IReadOnlyList<string> CapabilityNotes,
    string ConnectionNotice)
{
    public bool AxisRangesValid { get; init; }
    public string ClarityMetricVersion { get; init; } = "设备未提供";
    public string FactorySimpleFlow { get; init; } = "";
    public bool FactorySweepGearSupported { get; init; }
    public string FactorySamplingStrategy { get; init; } = "stationary";
}

public sealed record DeviceEvent(string Topic, string EventSequence, string BootId, string Json);

public sealed record ControlLeaseInfo(Guid LeaseId, string ControlEpoch, string Purpose, int LeaseTtlMilliseconds);

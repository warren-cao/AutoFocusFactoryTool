using FocusIR.Workbench.Domain;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FocusIR.Workbench.Application;

public interface IWorkbenchConnectionService : IAsyncDisposable
{
    ConnectionState State { get; }
    DeviceSummary? CurrentDevice { get; }
    event Action<ConnectionState>? StateChanged;
    event Action<DeviceSummary>? DeviceUpdated;
    event Action<JsonElement, bool>? SnapshotUpdated;
    event Action<ControlLeaseInfo?>? LeaseUpdated;
    event Action<DeviceEvent>? EventReceived;
    event Action<string>? Notice;

    Task<DeviceSummary> ConnectAsync(ConnectionSettings settings, CancellationToken cancellationToken = default);
    Task<ControlLeaseInfo> AcquireControlAsync(string purpose, CancellationToken cancellationToken = default);
    Task ReleaseControlAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> ValidateFactoryRecipeAsync(JsonObject recipe, CancellationToken cancellationToken = default);
    Task RefreshSnapshotAndResubscribeAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> StartFactoryCalibrationAsync(JsonObject recipe, string recipeSha256, CancellationToken cancellationToken = default);
    Task<JsonElement> GetFactoryResultAsync(string taskId, CancellationToken cancellationToken = default);
    Task<JsonElement> GetFactoryNodesAsync(string taskId, string cursor, int limit = 100, CancellationToken cancellationToken = default);
    Task<JsonElement> GetFactorySamplesAsync(string taskId, string cursor, int limit = 100, string? nodeId = null, CancellationToken cancellationToken = default);
    Task<JsonElement> ListLensModelsAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> CreateLensModelAsync(string lensModel, CancellationToken cancellationToken = default);
    Task<JsonElement> StartLensTaskAsync(string lensModel, bool initialize, int budgetMilliseconds = 180000, CancellationToken cancellationToken = default);
    Task<JsonElement> ListProfilesAsync(string lensModel, CancellationToken cancellationToken = default);
    Task<JsonElement> GetProfileAsync(string lensModel, string profileId, CancellationToken cancellationToken = default);
    Task<JsonElement> StartNamedFactoryCalibrationAsync(JsonObject recipe, string recipeSha256, string? profileId, string profileName, CancellationToken cancellationToken = default);
    Task<JsonElement> StartProfileTaskAsync(string lensModel, string profileId, bool verify, int budgetMilliseconds, CancellationToken cancellationToken = default);
    Task<JsonElement> UpdateProfileNodesAsync(string lensModel, string profileId, string expectedModelId, JsonArray nodes, CancellationToken cancellationToken = default);
    Task<JsonElement> GetTrackingAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> SetTrackingEnabledAsync(bool enabled, string expectedRevision, CancellationToken cancellationToken = default);
    Task<JsonElement> SetTrackingConfigurationAsync(bool enabled, string revision, int defaultGear, IReadOnlyList<double> ratios, CancellationToken cancellationToken = default);
    Task<JsonElement> StartTrackingAsync(int zoomPosition, bool verify, int budgetMilliseconds = 900000, CancellationToken cancellationToken = default);
    Task<JsonElement> StopTrackingAsync(string taskId, CancellationToken cancellationToken = default);
    Task<JsonElement> StartAutofocusAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> GetAutofocusConfigurationAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> SetAutofocusConfigurationAsync(double slowScore, string expectedRevision, CancellationToken cancellationToken = default);
    Task<JsonElement> StartSceneInitializationAsync(int budgetMilliseconds, CancellationToken cancellationToken = default);
    Task<JsonElement> GetTaskAsync(string taskId, CancellationToken cancellationToken = default);
    Task<JsonElement> CancelTaskAsync(string taskId, string reason, CancellationToken cancellationToken = default);
    Task<JsonElement> GetAutofocusResultAsync(string taskId, CancellationToken cancellationToken = default);
    Task<JsonElement> GetTemperatureAsync(CancellationToken cancellationToken = default);
    Task RefreshSnapshotAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> RequestStopAllAsync(string reason, CancellationToken cancellationToken = default);
    Task<JsonElement> GetRequestStatusAsync(Guid originalRequestId, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

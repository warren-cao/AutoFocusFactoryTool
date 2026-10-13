using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FocusIR.Workbench.Application;
using FocusIR.Workbench.Domain;
using FocusIR.Workbench.Protocol;

namespace FocusIR.Workbench.Infrastructure;

public sealed class WorkbenchConnectionService : IWorkbenchConnectionService
{
    private static readonly string[] RequiredAutofocusEventTopics = ["control", "task", "fault", "model", "config"];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DeviceStateStore _stateStore = new();
    private ProtocolClient? _client;
    private ConnectionSettings? _settings;
    private CancellationTokenSource? _reconnectCancellation;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task? _heartbeatTask;
    private CancellationTokenSource? _leaseCancellation;
    private Task? _leaseTask;
    private ControlLeaseInfo? _activeLease;
    private DeviceSummary? _currentDevice;
    private string[] _eventTopics = RequiredAutofocusEventTopics.ToArray();
    private int _telemetryHz;
    private ConnectionState _state = ConnectionState.Disconnected;
    private string? _lastBootId;
    private BigInteger _lastEventSequence;
    private int _reconnectRunning;
    private int _disposed;

    public ConnectionState State => _state;
    public DeviceSummary? CurrentDevice => _currentDevice;

    public event Action<ConnectionState>? StateChanged;
    public event Action<DeviceSummary>? DeviceUpdated;
    public event Action<JsonElement, bool>? SnapshotUpdated;
    public event Action<ControlLeaseInfo?>? LeaseUpdated;
    public event Action<DeviceEvent>? EventReceived;
    public event Action<string>? Notice;

    public async Task<DeviceSummary> ConnectAsync(ConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.Equals(settings.WorkbenchTag, ProtocolLimits.WorkbenchTag, StringComparison.Ordinal))
            throw new ArgumentException("工作台连接标识与当前协议不一致。", nameof(settings));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _reconnectCancellation?.Cancel();
            await CloseClientAsync().ConfigureAwait(false);
            _settings = settings;
            _reconnectCancellation?.Dispose();
            _reconnectCancellation = new CancellationTokenSource();
            SetState(ConnectionState.Connecting);
            var summary = await OpenSessionAsync(settings, isReconnect: false, cancellationToken).ConfigureAwait(false);
            _currentDevice = summary;
            return summary;
        }
        catch
        {
            /* A failed handshake can leave an unrecognized socket open. Cancel reconnect
               before disposing it so the disconnect callback cannot start a retry loop. */
            _reconnectCancellation?.Cancel();
            await CloseClientAsync().ConfigureAwait(false);
            SetState(ConnectionState.Faulted);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<JsonElement> GetRequestStatusAsync(Guid originalRequestId, CancellationToken cancellationToken = default)
    {
        var client = RequireConnectedClient();
        EnsureSupported("request.get_status");
        return GetRequestStatusEnvelopeAsync(client, originalRequestId, cancellationToken);
    }

    private async Task<JsonElement> GetRequestStatusEnvelopeAsync(ProtocolClient client, Guid originalRequestId,
        CancellationToken cancellationToken)
    {
        var response = await client.GetRequestStatusAsync(originalRequestId, cancellationToken).ConfigureAwait(false);
        ValidateResponseBootId(response, "request.get_status");
        return response;
    }

    public async Task<JsonElement> RequestStopAllAsync(string reason, CancellationToken cancellationToken = default)
    {
        var client = _client;
        var device = _currentDevice;
        if (_state != ConnectionState.Connected || client is null || device is null)
            throw new InvalidOperationException("设备未处于已连接状态，无法发送停止请求。");
        if (!device.SupportedMethods.Contains("motion.stop_all"))
            throw new NotSupportedException("设备未在 supported_methods 中声明 motion.stop_all。");
        if (device.Role is not ("operator" or "factory" or "engineer"))
            throw new UnauthorizedAccessException("当前设备角色没有软件停止权限。");

        var response = await RpcAsync(client, "motion.stop_all", new JsonObject
        {
            ["reason"] = string.IsNullOrWhiteSpace(reason) ? "operator_stop" : reason.Trim()
        }, cancellationToken).ConfigureAwait(false);
        var result = response.GetProperty("result").Clone();
        var returnedEpoch = GetStringOrNull(result, "control_epoch");
        if (returnedEpoch is not null)
            UpdateControlEpoch(returnedEpoch);
        return result;
    }

    public async Task<ControlLeaseInfo> AcquireControlAsync(string purpose, CancellationToken cancellationToken = default)
    {
        var client = RequireConnectedClient();
        var device = _currentDevice!;
        if (!device.SupportedMethods.Contains("control.acquire"))
            throw new NotSupportedException("设备未在 supported_methods 中声明 control.acquire。");
        if (device.Role is not ("operator" or "factory" or "engineer"))
            throw new UnauthorizedAccessException("当前设备角色没有获取控制权的权限。");
        if (purpose is not ("operation" or "scene_init" or "factory" or "maintenance"))
            throw new ArgumentOutOfRangeException(nameof(purpose));
        if (_activeLease is not null)
            throw new InvalidOperationException("本会话已经持有控制租约。");

        JsonElement response;
        try
        {
            response = await RpcAsync(client, "control.acquire", new JsonObject
            {
                ["purpose"] = purpose
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestTimeoutException ex)
        {
            /* The device may have granted a lease before the response was lost. Closing this
               session makes the device's disconnect policy revoke it; never repeat acquire. */
            await client.DisposeAsync().ConfigureAwait(false);
            throw new IOException($"control.acquire 超时（request_id {ex.RequestId}）；已关闭会话以触发设备端撤销，不会重发申请。", ex);
        }
        var result = response.GetProperty("result");
        var leaseId = ReadGuid(result, "lease_id");
        var epoch = GetStringOrNull(result, "control_epoch")
            ?? throw new ProtocolException("control.acquire returned no decimal-string control_epoch.");
        _ = ParseUInt64String(epoch);
        var ttl = result.TryGetProperty("lease_ttl_ms", out var ttlElement) && TryGetInt32Value(ttlElement, out var ttlValue)
            ? Math.Clamp(ttlValue, 1000, 60_000)
            : 5000;
        var lease = new ControlLeaseInfo(leaseId, epoch, purpose, ttl);
        _activeLease = lease;
        LeaseUpdated?.Invoke(lease);
        StartLeaseRenewal(client, lease);
        return lease;
    }

    public async Task ReleaseControlAsync(CancellationToken cancellationToken = default)
    {
        var lease = _activeLease;
        if (lease is null)
            return;
        await StopLeaseRenewalAsync().ConfigureAwait(false);
        try
        {
            var client = RequireConnectedClient();
            await RpcAsync(client, "control.release", new JsonObject
            {
                ["lease_id"] = lease.LeaseId.ToString()
            }, cancellationToken, lease, timeout: TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        finally
        {
            ClearControlLease();
        }
    }

    public async Task<JsonElement> ValidateFactoryRecipeAsync(JsonObject recipe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var client = RequireConnectedClient();
        EnsureSupported("factory.validate_recipe");
        var response = await RpcAsync(client, "factory.validate_recipe", new JsonObject
        {
            ["recipe"] = recipe.DeepClone()
        }, cancellationToken).ConfigureAwait(false);
        return response.GetProperty("result").Clone();
    }

    public async Task RefreshSnapshotAndResubscribeAsync(CancellationToken cancellationToken = default)
    {
        var client = RequireConnectedClient();
        EnsureSupported("state.get_snapshot");
        await RefreshSnapshotCoreAsync(cancellationToken).ConfigureAwait(false);
        await SubscribeAsync(client, _lastEventSequence.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> StartFactoryCalibrationAsync(JsonObject recipe, string recipeSha256, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (string.IsNullOrWhiteSpace(recipeSha256))
            throw new ArgumentException("factory.start 必须使用 factory.validate_recipe 返回的 recipe_sha256。", nameof(recipeSha256));
        var client = RequireConnectedClient();
        EnsureSupported("factory.start");
        var lease = RequireControlLease("factory");
        return await StartTaskWithStatusResolutionAsync(client, "factory.start", new JsonObject
        {
            ["recipe"] = recipe.DeepClone(),
            ["recipe_sha256"] = recipeSha256
        }, lease, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> GetFactoryResultAsync(string taskId, CancellationToken cancellationToken = default)
    {
        EnsureTaskId(taskId);
        var client = RequireConnectedClient();
        EnsureSupported("factory.get_result");
        var response = await RpcAsync(client, "factory.get_result", new JsonObject
        {
            ["task_id"] = taskId
        }, cancellationToken).ConfigureAwait(false);
        return response.GetProperty("result").Clone();
    }

    public async Task<JsonElement> ListLensModelsAsync(CancellationToken cancellationToken = default)
    {
        EnsureSupported("lens.list");
        var response = await RpcAsync(RequireConnectedClient(), "lens.list", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var result = response.GetProperty("result").Clone();
        if(_currentDevice is { } device) {
            _currentDevice = device with { LensModel = GetStringOrNull(result,"active_lens_model") is { Length: > 0 } active ? active : "尚未激活" };
            DeviceUpdated?.Invoke(_currentDevice);
        }
        return result;
    }

    public async Task<JsonElement> CreateLensModelAsync(string lensModel, CancellationToken cancellationToken = default)
    {
        EnsureSupported("lens.create");
        var response = await RpcAsync(RequireConnectedClient(), "lens.create", new JsonObject { ["lens_model"] = lensModel }, cancellationToken).ConfigureAwait(false);
        return response.GetProperty("result").Clone();
    }

    public Task<JsonElement> StartLensTaskAsync(string lensModel, bool initialize, int budgetMilliseconds = 180000, CancellationToken cancellationToken = default)
    {
        if (initialize && (budgetMilliseconds is < 1 or > 600000))
            throw new ArgumentOutOfRangeException(nameof(budgetMilliseconds), "镜头初始化预算必须为 1～600000 ms。");
        var method = initialize ? "lens.initialize" : "lens.activate";
        EnsureSupported(method);
        var lease = RequireControlLease(initialize ? "scene_init" : "factory");
        var parameters = new JsonObject { ["lens_model"] = lensModel };
        if (initialize) parameters["budget_ms"] = budgetMilliseconds;
        return StartTaskWithStatusResolutionAsync(RequireConnectedClient(), method, parameters, lease, cancellationToken);
    }
    public async Task<JsonElement> ListProfilesAsync(string lensModel, CancellationToken cancellationToken = default)
    {
        EnsureSupported("profile.list");var response=await RpcAsync(RequireConnectedClient(),"profile.list",new JsonObject { ["lens_model"] = lensModel },cancellationToken).ConfigureAwait(false);
        return response.GetProperty("result").Clone();
    }
    public async Task<JsonElement> GetProfileAsync(string lensModel,string profileId,CancellationToken cancellationToken = default)
    {
        EnsureSupported("profile.get");var response=await RpcAsync(RequireConnectedClient(),"profile.get",new JsonObject { ["lens_model"]=lensModel,["profile_id"]=profileId },cancellationToken).ConfigureAwait(false);
        return response.GetProperty("result").Clone();
    }
    public Task<JsonElement> StartNamedFactoryCalibrationAsync(JsonObject recipe,string recipeSha256,string? profileId,string profileName,CancellationToken cancellationToken = default)
    {
        EnsureSupported("profile.list");EnsureSupported("factory.start");var lease=RequireControlLease("factory");
        var parameters=new JsonObject { ["recipe"]=recipe.DeepClone(),["recipe_sha256"]=recipeSha256,["profile_name"]=profileName };
        if(!string.IsNullOrWhiteSpace(profileId)) parameters["profile_id"]=profileId;
        return StartTaskWithStatusResolutionAsync(RequireConnectedClient(),"factory.start",parameters,lease,cancellationToken);
    }
    public Task<JsonElement> StartProfileTaskAsync(string lensModel,string profileId,bool verify,int budgetMilliseconds,CancellationToken cancellationToken = default)
    {
        var method=verify?"profile.verify":"profile.activate";EnsureSupported(method);var lease=RequireControlLease("factory");
        return StartTaskWithStatusResolutionAsync(RequireConnectedClient(),method,new JsonObject { ["lens_model"]=lensModel,["profile_id"]=profileId,["budget_ms"]=budgetMilliseconds },lease,cancellationToken);
    }

    public Task<JsonElement> UpdateProfileNodesAsync(string lensModel, string profileId, string expectedModelId, JsonArray nodes, CancellationToken cancellationToken = default)
    {
        EnsureSupported("profile.update_nodes");
        var lease=RequireControlLease("factory");
        return StartTaskWithStatusResolutionAsync(RequireConnectedClient(),"profile.update_nodes",new JsonObject {
            ["lens_model"]=lensModel,["profile_id"]=profileId,["expected_model_id"]=expectedModelId,["nodes"]=nodes.DeepClone(),["budget_ms"]=30000
        },lease,cancellationToken);
    }

    public async Task<JsonElement> GetTrackingAsync(CancellationToken cancellationToken = default)
    {
        var client = RequireConnectedClient(); EnsureSupported("tracking.get");
        var response = await RpcAsync(client, "tracking.get", new JsonObject(), cancellationToken).ConfigureAwait(false);
        return response.GetProperty("result").Clone();
    }

    public async Task<JsonElement> SetTrackingEnabledAsync(bool enabled, string expectedRevision, CancellationToken cancellationToken = default)
    {
        var client = RequireConnectedClient(); EnsureSupported("tracking.set");
        var lease = RequireControlLease("operation");
        var response = await RpcAsync(client, "tracking.set", new JsonObject {
            ["enabled"] = enabled, ["expected_revision"] = expectedRevision
        }, cancellationToken, lease).ConfigureAwait(false);
        return response.GetProperty("result").Clone();
    }

    public async Task<JsonElement> SetTrackingConfigurationAsync(bool enabled, string revision, int defaultGear, IReadOnlyList<double> ratios, CancellationToken cancellationToken = default)
    {
        var client=RequireConnectedClient(); EnsureSupported("tracking.set");
        var lease=RequireControlLease("operation");
        if(defaultGear is < 1 or > 9 || ratios.Count!=9 || ratios.Any(x=>!double.IsFinite(x) || x<0.1 || x>3.0))
            throw new ArgumentException("默认档位须为1～9，九个速度比例须为0.1～3.0。");
        var values=new JsonArray(); foreach(var ratio in ratios) values.Add(ratio);
        var response=await RpcAsync(client,"tracking.set",new JsonObject {
            ["enabled"]=enabled,["expected_revision"]=revision,["default_zoom_gear"]=defaultGear,["focus_speed_ratios"]=values
        },cancellationToken,lease).ConfigureAwait(false);
        return response.GetProperty("result").Clone();
    }

    public Task<JsonElement> StartTrackingAsync(int zoomPosition, bool verify, int budgetMilliseconds = 900000, CancellationToken cancellationToken = default)
    {
        var client = RequireConnectedClient(); var method = verify ? "tracking.verify" : "tracking.move";
        EnsureSupported(method); var lease = RequireControlLease("operation");
        var parameters = new JsonObject { ["budget_ms"] = budgetMilliseconds };
        if (!verify) parameters["zoom_position"] = zoomPosition;
        return StartTaskWithStatusResolutionAsync(client, method, parameters, lease, cancellationToken);
    }

    public async Task<JsonElement> StopTrackingAsync(string taskId, CancellationToken cancellationToken = default)
    {
        EnsureTaskId(taskId); var client = RequireConnectedClient(); EnsureSupported("tracking.stop");
        var response = await RpcAsync(client, "tracking.stop", new JsonObject { ["task_id"] = taskId },
            cancellationToken, RequireControlLease("operation")).ConfigureAwait(false);
        return response.GetProperty("result").Clone();
    }

    public async Task<JsonElement> GetAutofocusConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var client = RequireConnectedClient(); EnsureSupported("af.get_config");
        var response = await RpcAsync(client, "af.get_config", new JsonObject(), cancellationToken).ConfigureAwait(false);
        return response.GetProperty("result").Clone();
    }

    public async Task<JsonElement> SetAutofocusConfigurationAsync(double slowScore, string expectedRevision, CancellationToken cancellationToken = default)
    {
        var client = RequireConnectedClient(); EnsureSupported("af.set_config");
        if (!double.IsFinite(slowScore) || slowScore <= 0 || slowScore > 1000000)
            throw new ArgumentOutOfRangeException(nameof(slowScore), "自动聚焦减速阈值须大于 0 且不超过 1000000。");
        if (!ulong.TryParse(expectedRevision, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("设备未返回有效的自动聚焦配置版本。", nameof(expectedRevision));
        var response = await RpcAsync(client, "af.set_config", new JsonObject {
            ["slow_score"] = slowScore, ["expected_revision"] = expectedRevision
        }, cancellationToken, RequireControlLease("factory")).ConfigureAwait(false);
        return response.GetProperty("result").Clone();
    }

    public async Task<JsonElement> StartAutofocusAsync(CancellationToken cancellationToken = default)
    {
        var client = RequireConnectedClient();
        var lease = RequireControlLease();
        EnsureSupported("af.start");
        if (lease.Purpose != "operation")
            throw new InvalidOperationException("af.start requires an operation-purpose control lease.");
        return await StartTaskWithStatusResolutionAsync(client, "af.start", new JsonObject
        {
            ["search_mode"] = "local_then_recovery"
        }, lease, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> StartSceneInitializationAsync(int budgetMilliseconds, CancellationToken cancellationToken = default)
    {
        var client = RequireConnectedClient();
        var lease = RequireControlLease();
        EnsureSupported("scene.initialize");
        if (lease.Purpose != "scene_init")
            throw new InvalidOperationException("scene.initialize requires a scene_init-purpose control lease.");
        if (budgetMilliseconds is < 1 or > 180_000)
            throw new ArgumentOutOfRangeException(nameof(budgetMilliseconds), "场景初始化预算必须为 1–180000 ms。");
        return await StartTaskWithStatusResolutionAsync(client, "scene.initialize", new JsonObject
        {
            ["budget_ms"] = budgetMilliseconds,
            ["coverage"] = "full_optical_range"
        }, lease, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> StartTaskWithStatusResolutionAsync(ProtocolClient client, string method,
        JsonObject parameters, ControlLeaseInfo lease, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        JsonElement response;
        try
        {
            response = await RpcAsync(client, method, parameters, cancellationToken, lease, requestId).ConfigureAwait(false);
        }
        catch (RequestTimeoutException)
        {
            /* Resolve the original idempotency key; never create a second physical task. */
            JsonElement status;
            try
            {
                status = await GetRequestStatusEnvelopeAsync(client, requestId, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestTimeoutException)
            {
                throw new RequestTimeoutException(requestId, method);
            }
            if (status.TryGetProperty("status", out var statusKind) && statusKind.GetString() == "ok"
                && status.TryGetProperty("result", out var statusResult)
                && statusResult.TryGetProperty("original_response", out var original))
            {
                if (original.TryGetProperty("status", out var originalStatus) && originalStatus.GetString() == "error")
                {
                    var error = original.GetProperty("error");
                    throw new DeviceRpcException(method, GetStringOrNull(error, "code") ?? "INTERNAL_ERROR",
                        GetStringOrNull(error, "message") ?? "设备未接受任务。");
                }
                response = original;
            }
            else
            {
                throw new RequestTimeoutException(requestId, method);
            }
        }
        return response.GetProperty("result").Clone();
    }

    public Task<JsonElement> GetFactoryNodesAsync(string taskId, string cursor, int limit = 100, CancellationToken cancellationToken = default)
        => GetFactoryPageAsync("factory.get_nodes", taskId, cursor, limit, null, cancellationToken);

    public Task<JsonElement> GetFactorySamplesAsync(string taskId, string cursor, int limit = 100, string? nodeId = null, CancellationToken cancellationToken = default)
        => GetFactoryPageAsync("factory.get_samples", taskId, cursor, limit, nodeId, cancellationToken);

    private async Task<JsonElement> GetFactoryPageAsync(string method, string taskId, string cursor, int limit, string? nodeId, CancellationToken cancellationToken)
    {
        EnsureTaskId(taskId);
        EnsureSupported(method);
        if (limit is < 1 or > 100 || !ulong.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("分页游标必须为无符号十进制数，分页数量为 1–100。");
        var parameters = new JsonObject { ["task_id"] = taskId, ["cursor"] = cursor, ["limit"] = limit };
        if (nodeId is not null) parameters["node_id"] = nodeId;
        var response = await RpcAsync(RequireConnectedClient(), method, parameters, cancellationToken).ConfigureAwait(false);
        return response.GetProperty("result").Clone();
    }

    public async Task<JsonElement> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        EnsureTaskId(taskId);
        var client = RequireConnectedClient();
        EnsureSupported("task.get");
        var response = await RpcAsync(client, "task.get", new JsonObject
        {
            ["task_id"] = taskId
        }, cancellationToken).ConfigureAwait(false);
        return response.GetProperty("result").Clone();
    }

    public async Task<JsonElement> CancelTaskAsync(string taskId, string reason, CancellationToken cancellationToken = default)
    {
        EnsureTaskId(taskId);
        var client = RequireConnectedClient();
        EnsureSupported("task.cancel");
        var response = await RpcAsync(client, "task.cancel", new JsonObject
        {
            ["task_id"] = taskId,
            ["reason"] = string.IsNullOrWhiteSpace(reason) ? "operator_cancel" : reason.Trim()
        }, cancellationToken, RequireControlLease()).ConfigureAwait(false);
        var result = response.GetProperty("result").Clone();
        var returnedEpoch = GetStringOrNull(result, "control_epoch");
        if (returnedEpoch is not null)
            UpdateControlEpoch(returnedEpoch);
        return result;
    }

    public async Task<JsonElement> GetAutofocusResultAsync(string taskId, CancellationToken cancellationToken = default)
    {
        EnsureTaskId(taskId);
        var client = RequireConnectedClient();
        EnsureSupported("af.get_result");
        var response = await RpcAsync(client, "af.get_result", new JsonObject
        {
            ["task_id"] = taskId
        }, cancellationToken).ConfigureAwait(false);
        return response.GetProperty("result").Clone();
    }

    public async Task<JsonElement> GetTemperatureAsync(CancellationToken cancellationToken = default)
    {
        var client = RequireConnectedClient();
        EnsureSupported("temperature.get");
        var response = await RpcAsync(client, "temperature.get", new JsonObject(), cancellationToken).ConfigureAwait(false);
        return response.GetProperty("result").Clone();
    }

    public Task RefreshSnapshotAsync(CancellationToken cancellationToken = default) => RefreshSnapshotCoreAsync(cancellationToken);

    private async Task<DeviceSummary> OpenSessionAsync(ConnectionSettings settings, bool isReconnect, CancellationToken cancellationToken)
    {
        var client = await ProtocolClient.ConnectAsync(settings.Host, settings.Port, cancellationToken).ConfigureAwait(false);
        client.EventReceived += OnProtocolEvent;
        client.Disconnected += OnDisconnected;
        _client = client;

        var opened = await RpcAsync(client, "session.open", new JsonObject
        {
            ["client_id"] = settings.ClientId.ToString(),
            ["client_name"] = settings.ClientName,
            ["client_version"] = settings.ClientVersion,
            ["supported_versions"] = new JsonArray(JsonValue.Create(ProtocolLimits.Version)),
            ["workbench_tag"] = settings.WorkbenchTag
        }, cancellationToken).ConfigureAwait(false);

        var result = opened.GetProperty("result");
        if (!string.Equals(GetStringOrNull(result, "workbench_tag"), ProtocolLimits.WorkbenchTag, StringComparison.Ordinal))
            throw new ProtocolException("设备返回的工作台连接标识不一致，请使用配套的设备版本。");
        var sessionId = ReadGuid(result, "session_id");
        var bootId = result.GetProperty("boot_id").GetString() ?? throw new ProtocolException("session.open returned no boot_id.");
        var heartbeatIntervalMs = result.TryGetProperty("heartbeat_interval_ms", out var heartbeatInterval) && TryGetInt32Value(heartbeatInterval, out var negotiatedInterval)
            ? Math.Clamp(negotiatedInterval, 250, 10_000)
            : 3_000;
        client.SetSessionId(sessionId);

        if (_lastBootId is not null && !string.Equals(_lastBootId, bootId, StringComparison.Ordinal))
        {
            _lastEventSequence = BigInteger.Zero;
            Notice?.Invoke("设备控制服务的 boot_id 已变化；所有旧任务请求均保持不重放，请先检查设备状态。");
        }
        _lastBootId = bootId;

        var capabilitiesResponse = await RpcAsync(client, "device.get_capabilities", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var capabilities = capabilitiesResponse.GetProperty("result");
        var supported = ReadStringSet(capabilities, "supported_methods");

        JsonElement? info = null;
        if (supported.Contains("device.get_info"))
        {
            try
            {
                info = (await RpcAsync(client, "device.get_info", new JsonObject(), cancellationToken).ConfigureAwait(false)).GetProperty("result").Clone();
            }
            catch (DeviceRpcException ex) when (ex.Code == "NOT_SUPPORTED")
            {
                Notice?.Invoke("设备虽声明 device.get_info，但当前服务返回 NOT_SUPPORTED；将以快照和能力响应继续连接。");
            }
        }

        var snapshotResponse = await RpcAsync(client, "state.get_snapshot", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var snapshot = snapshotResponse.GetProperty("result");
        var snapshotSeq = snapshot.TryGetProperty("snapshot_seq", out var sequence) && sequence.ValueKind == JsonValueKind.String
            ? sequence.GetString() ?? "0"
            : throw new ProtocolException("state.get_snapshot returned no decimal-string snapshot_seq.");
        _stateStore.ReplaceSnapshot(snapshot, bootId);
        _lastEventSequence = ParseUInt64String(snapshotSeq);
        PublishSnapshot(isFreshDeviceSnapshot: true);

        if (capabilities.TryGetProperty("supported_event_topics", out var topicElement)
            && topicElement.ValueKind != JsonValueKind.Array)
            throw new ProtocolException("supported_event_topics must be an array when present.");
        if (capabilities.TryGetProperty("supported_event_topics", out topicElement))
        {
            var advertisedTopics = ReadStringArray(topicElement);
            var missingTopics = RequiredAutofocusEventTopics
                .Where(topic => !advertisedTopics.Contains(topic, StringComparer.Ordinal))
                .ToArray();
            if (missingTopics.Length > 0)
                Notice?.Invoke($"设备能力未声明 AF 联调所需事件主题：{string.Join(", ", missingTopics)}。订阅仍按 v1 要求发送五个可靠主题。");
        }
        /* Controlled AF deliberately subscribes to exactly the five reliable topics. High-rate
           telemetry is not part of this round and must not be enabled implicitly. */
        _eventTopics = RequiredAutofocusEventTopics.ToArray();
        _telemetryHz = 0;

        var dataSourceName = GetStringOrNull(info, "data_source");
        var dataSource = dataSourceName == "simulator"
            ? DataSourceKind.Simulator
            : dataSourceName == "device"
                ? DataSourceKind.Device
                : DataSourceKind.Unknown;
        var summary = new DeviceSummary(
            GetStringOrNull(info, "serial_number") ?? "设备未提供",
            GetStringOrNull(info, "lens_model") ?? "设备未提供",
            GetStringOrNull(info, "firmware_version") ?? "设备未提供",
            GetStringOrNull(info, "service_version") ?? "设备未提供",
            GetStringOrNull(result, "role") ?? "未知",
            bootId,
            snapshotSeq,
            dataSource,
            supported,
            BuildCapabilityNotes(capabilities),
            isReconnect ? "已重新连接；未恢复控制权、手动运动或旧任务。" : "已连接；尚未获取控制租约。"
        )
        {
            AxisRangesValid = AreAxisRangesValid(capabilities),
            ClarityMetricVersion = GetStringOrNull(info, "clarity_metric_version") ?? "设备未提供",
            FactorySimpleFlow = GetStringOrNull(capabilities, "factory_simple_flow") ?? "",
            FactorySweepGearSupported = ReadBooleanOrNull(capabilities, "factory_sweep_gear_supported") == true,
            FactorySamplingStrategy = GetStringOrNull(capabilities, "factory_sampling_strategy") ?? "stationary"
        };

        _currentDevice = summary;
        DeviceUpdated?.Invoke(summary);
        SetState(ConnectionState.Connected);
        await SubscribeAsync(client, snapshotSeq, cancellationToken).ConfigureAwait(false);
        StartHeartbeat(client, heartbeatIntervalMs);
        return summary;
    }

    private void StartHeartbeat(ProtocolClient client, int intervalMilliseconds)
    {
        _heartbeatCancellation?.Cancel();
        _heartbeatCancellation?.Dispose();
        _heartbeatCancellation = new CancellationTokenSource();
        _heartbeatTask = HeartbeatLoopAsync(client, intervalMilliseconds, _heartbeatCancellation.Token);
    }

    private async Task HeartbeatLoopAsync(ProtocolClient client, int intervalMilliseconds, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(intervalMilliseconds), cancellationToken).ConfigureAwait(false);
                if (!ReferenceEquals(_client, client))
                    return;
                await RpcAsync(client, "session.ping", new JsonObject
                {
                    ["client_mono_ms"] = Environment.TickCount64.ToString(CultureInfo.InvariantCulture)
                }, cancellationToken, null, null,
                    TimeSpan.FromMilliseconds(Math.Clamp(intervalMilliseconds * 2, 3_000, 8_000))).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Notice?.Invoke($"会话心跳未确认：{ex.Message}；正在关闭连接并重连。");
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task SubscribeAsync(ProtocolClient client, string afterSequence, CancellationToken cancellationToken)
    {
        if (_eventTopics.Length == 0)
        {
            Notice?.Invoke("设备没有公布可靠事件主题；当前仅显示连接时读取的状态快照。");
            return;
        }
        try
        {
            await RpcAsync(client, "event.subscribe", CreateSubscriptionParameters(afterSequence), cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceRpcException ex) when (ex.Code == "EVENT_GAP")
        {
            Notice?.Invoke("事件游标已过期，正在刷新设备快照。");
            var snapshotResponse = await RpcAsync(client, "state.get_snapshot", new JsonObject(), cancellationToken).ConfigureAwait(false);
            var snapshot = snapshotResponse.GetProperty("result");
            var sequence = snapshot.GetProperty("snapshot_seq").GetString() ?? throw new ProtocolException("Snapshot sequence is missing.");
            _stateStore.ReplaceSnapshot(snapshot, _lastBootId ?? string.Empty);
            _lastEventSequence = ParseUInt64String(sequence);
            PublishSnapshot(isFreshDeviceSnapshot: true);
            if (_currentDevice is not null && _currentDevice.BootId == _lastBootId)
            {
                _currentDevice = _currentDevice with { SnapshotSequence = sequence };
                DeviceUpdated?.Invoke(_currentDevice);
            }
            await RpcAsync(client, "event.subscribe", CreateSubscriptionParameters(sequence), cancellationToken).ConfigureAwait(false);
        }
    }

    private JsonObject CreateSubscriptionParameters(string afterSequence)
    {
        var topics = new JsonArray();
        foreach (var topic in _eventTopics)
            topics.Add(JsonValue.Create(topic));
        var parameters = new JsonObject
        {
            ["topics"] = topics,
            ["after_seq"] = afterSequence
        };
        if (_telemetryHz > 0)
            parameters["telemetry_hz"] = _telemetryHz;
        return parameters;
    }

    private async Task<JsonElement> RpcAsync(ProtocolClient client, string method, JsonObject parameters, CancellationToken cancellationToken)
        => await RpcAsync(client, method, parameters, cancellationToken, null, null).ConfigureAwait(false);

    private async Task<JsonElement> RpcAsync(ProtocolClient client, string method, JsonObject parameters, CancellationToken cancellationToken, ControlLeaseInfo? lease, Guid? requestId = null, TimeSpan? timeout = null)
    {
        var response = await client.RequestAsync(method, parameters,
            requestId: requestId,
            leaseId: lease?.LeaseId,
            controlEpoch: lease?.ControlEpoch,
            timeout: timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        ValidateResponseBootId(response, method);
        if (!response.TryGetProperty("status", out var status))
            throw new ProtocolException($"{method} response has no status.");
        if (status.GetString() == "error")
        {
            var error = response.GetProperty("error");
            var code = GetStringOrNull(error, "code") ?? "INTERNAL_ERROR";
            var message = GetStringOrNull(error, "message") ?? "设备请求失败。";
            throw new DeviceRpcException(method, code, message);
        }
        return response;
    }

    private void ValidateResponseBootId(JsonElement response, string method)
    {
        var responseBootId = GetStringOrNull(response, "boot_id")
            ?? throw new ProtocolException($"{method} response has no boot_id.");
        if (method != "session.open" && _lastBootId is not null
            && !string.Equals(_lastBootId, responseBootId, StringComparison.Ordinal))
        {
            _lastBootId = responseBootId;
            _lastEventSequence = BigInteger.Zero;
            _leaseCancellation?.Cancel();
            if (_activeLease is not null)
                ClearControlLease();
            Notice?.Invoke("响应中的 boot_id 已变化；旧控制租约和运动意图已作废，请先刷新设备状态。");
            if (method != "state.get_snapshot")
                throw new ProtocolException($"{method} response belongs to a new device boot lifecycle.");
        }
    }

    private ProtocolClient RequireConnectedClient()
    {
        if (_state != ConnectionState.Connected || _client is null || _currentDevice is null)
            throw new InvalidOperationException("设备当前未连接。");
        return _client;
    }

    private ControlLeaseInfo RequireControlLease(string? requiredPurpose = null)
    {
        var lease = _activeLease
            ?? throw new InvalidOperationException("当前会话没有控制租约；请先显式获取操作权。");
        if (requiredPurpose is not null && !string.Equals(lease.Purpose, requiredPurpose, StringComparison.Ordinal))
            throw new InvalidOperationException($"当前控制租约用途为 {lease.Purpose}，{requiredPurpose} 操作需要用途为 {requiredPurpose} 的租约。");
        return lease;
    }

    private void EnsureSupported(string method)
    {
        var device = _currentDevice;
        if (device is null || !device.SupportedMethods.Contains(method))
            throw new NotSupportedException($"设备未在 supported_methods 中声明 {method}。");
    }

    private void StartLeaseRenewal(ProtocolClient client, ControlLeaseInfo lease)
    {
        _leaseCancellation?.Cancel();
        _leaseCancellation?.Dispose();
        _leaseCancellation = new CancellationTokenSource();
        _leaseTask = LeaseRenewalLoopAsync(client, lease, _leaseCancellation.Token);
    }

    private async Task LeaseRenewalLoopAsync(ProtocolClient client, ControlLeaseInfo lease, CancellationToken cancellationToken)
    {
        var currentLease = lease;
        try
        {
            var interval = Math.Clamp(currentLease.LeaseTtlMilliseconds / 5, 250, 1000);
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                if (!ReferenceEquals(_client, client))
                    return;
                var activeLease = _activeLease;
                if (activeLease is null || activeLease.LeaseId != currentLease.LeaseId)
                    return;
                currentLease = activeLease;
                var response = await RpcAsync(client, "control.renew", new JsonObject
                {
                    ["lease_id"] = currentLease.LeaseId.ToString()
                }, cancellationToken, currentLease, timeout: TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var result = response.GetProperty("result");
                /* 当前设备 control.renew 成功响应的 result 为空；若未来返回新的 epoch，
                   立即采用它。epoch 变化也会通过 control 事件触发清理。 */
                var renewedEpoch = GetStringOrNull(result, "control_epoch");
                if (renewedEpoch is null)
                    continue;
                _ = ParseUInt64String(renewedEpoch);
                if (renewedEpoch != currentLease.ControlEpoch)
                {
                    var updatedLease = currentLease with { ControlEpoch = renewedEpoch };
                    if (_activeLease is not { } active || active.LeaseId != currentLease.LeaseId)
                        return;
                    _activeLease = updatedLease;
                    currentLease = updatedLease;
                    LeaseUpdated?.Invoke(updatedLease);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_activeLease, currentLease))
            {
                _activeLease = null;
                LeaseUpdated?.Invoke(null);
                Notice?.Invoke($"控制租约续期未确认，已禁用需要控制权的操作：{ex.Message}");
            }
        }
    }

    private async Task StopLeaseRenewalAsync()
    {
        _leaseCancellation?.Cancel();
        if (_leaseTask is not null)
        {
            try { await _leaseTask.ConfigureAwait(false); }
            catch { }
            _leaseTask = null;
        }
        _leaseCancellation?.Dispose();
        _leaseCancellation = null;
    }

    private void ClearControlLease()
    {
        _activeLease = null;
        LeaseUpdated?.Invoke(null);
    }

    private void UpdateControlEpoch(string epoch)
    {
        _ = ParseUInt64String(epoch);
        if (_activeLease is not { } lease || string.Equals(lease.ControlEpoch, epoch, StringComparison.Ordinal))
            return;

        var updatedLease = lease with { ControlEpoch = epoch };
        _activeLease = updatedLease;
        LeaseUpdated?.Invoke(updatedLease);
    }

    private static void EnsureTaskId(string taskId)
    {
        if (!ulong.TryParse(taskId, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && !Guid.TryParse(taskId, out _))
            throw new ArgumentException("task_id 必须是设备十进制 ID 或模拟器 UUID 字符串。", nameof(taskId));
    }

    private void OnProtocolEvent(JsonElement message)
    {
        try
        {
            var bootId = GetStringOrNull(message, "boot_id") ?? string.Empty;
            var sequenceText = GetStringOrNull(message, "event_seq") ?? "0";
            var sequence = ParseUInt64String(sequenceText);
            var topic = GetStringOrNull(message, "topic") ?? "unknown";

            if (topic.StartsWith("control", StringComparison.Ordinal)
                && _activeLease is { } lease
                && message.TryGetProperty("data", out var controlData)
                && controlData.ValueKind == JsonValueKind.Object)
            {
                var eventEpoch = GetStringOrNull(controlData, "control_epoch");
                var explicitlyReleased = controlData.TryGetProperty("held", out var held)
                    && held.ValueKind == JsonValueKind.False;
                var ownerId = GetStringOrNull(controlData, "owner_id");
                var localClientId = _settings?.ClientId.ToString();
                var isLocalOwner = ownerId is not null && localClientId is not null
                    && string.Equals(ownerId, localClientId, StringComparison.OrdinalIgnoreCase);
                if (explicitlyReleased)
                {
                    _leaseCancellation?.Cancel();
                    ClearControlLease();
                }
                else if (eventEpoch is not null && eventEpoch != lease.ControlEpoch)
                {
                    if (isLocalOwner)
                        UpdateControlEpoch(eventEpoch);
                    else
                    {
                        _leaseCancellation?.Cancel();
                        ClearControlLease();
                    }
                }
            }

            var isSampleStream = GetStringOrNull(message, "kind") == "telemetry"
                || ((topic is "telemetry" or "focus.samples" or "calibration.nodes") && message.TryGetProperty("sample_seq", out _));
            if (isSampleStream)
            {
                if (_stateStore.ApplyTelemetry(message))
                    EventReceived?.Invoke(new DeviceEvent(topic, GetStringOrNull(message, "sample_seq") ?? "0", bootId, message.GetRawText()));
                return;
            }

            if (!string.Equals(bootId, _lastBootId, StringComparison.Ordinal))
            {
                Notice?.Invoke("收到不同 boot_id 的事件；旧控制意图已作废，请刷新设备快照。");
                _leaseCancellation?.Cancel();
                if (_activeLease is not null)
                    ClearControlLease();
                _lastBootId = bootId;
                _lastEventSequence = BigInteger.Zero;
                _ = ResynchronizeAsync();
                return;
            }

            if (sequence > _lastEventSequence + BigInteger.One)
            {
                Notice?.Invoke("可靠事件出现缺口，正在重新读取状态快照。");
                _ = ResynchronizeAsync();
                return;
            }
            if (sequence <= _lastEventSequence)
                return;

            var applyResult = _stateStore.ApplyReliableEvent(message);
            if (applyResult is EventApplyResult.Gap or EventApplyResult.BootChanged)
            {
                Notice?.Invoke("可靠事件无法与当前快照连续合并，正在重新读取状态。");
                _ = ResynchronizeAsync();
                return;
            }
            if (applyResult == EventApplyResult.Duplicate)
                return;

            _lastEventSequence = sequence;
            PublishSnapshot(isFreshDeviceSnapshot: false);
            if (_currentDevice is not null)
            {
                _currentDevice = _currentDevice with { SnapshotSequence = sequenceText };
                DeviceUpdated?.Invoke(_currentDevice);
            }
            EventReceived?.Invoke(new DeviceEvent(topic, sequenceText, bootId, message.GetRawText()));
        }
        catch (Exception ex)
        {
            Notice?.Invoke($"事件无法合并：{ex.Message}");
        }
    }

    private async Task ResynchronizeAsync()
    {
        try
        {
            await RefreshSnapshotCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Notice?.Invoke($"状态刷新失败：{ex.Message}");
        }
    }

    private async Task RefreshSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is null || _state != ConnectionState.Connected)
                return;
            var client = _client;
            var snapshot = await RpcAsync(client, "state.get_snapshot", new JsonObject(), cancellationToken).ConfigureAwait(false);
            var snapshotResult = snapshot.GetProperty("result");
            var sequence = snapshotResult.GetProperty("snapshot_seq").GetString() ?? "0";
            var bootId = GetStringOrNull(snapshot, "boot_id")
                ?? throw new ProtocolException("state.get_snapshot response has no boot_id.");
            if (_lastBootId is not null && !string.Equals(_lastBootId, bootId, StringComparison.Ordinal))
            {
                _leaseCancellation?.Cancel();
                ClearControlLease();
                _lastEventSequence = BigInteger.Zero;
                Notice?.Invoke("刷新快照时发现设备 boot_id 已变化；旧任务和控制租约已作废，请重新检查设备状态。");
            }
            _lastBootId = bootId;
            _stateStore.ReplaceSnapshot(snapshotResult, bootId);
            _lastEventSequence = ParseUInt64String(sequence);
            PublishSnapshot(isFreshDeviceSnapshot: true);
            if (_currentDevice is not null)
            {
                _currentDevice = _currentDevice with { SnapshotSequence = sequence, BootId = bootId };
                DeviceUpdated?.Invoke(_currentDevice);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnDisconnected(Exception? failure)
    {
        if (failure is not null && _activeLease is not null)
            Notice?.Invoke("Socket 已失联，无法在断开的连接上发送 motion.stop_all；设备端失联停止策略和现场急停必须继续确认。");
        _leaseCancellation?.Cancel();
        if (_activeLease is not null)
            ClearControlLease();
        if (_state == ConnectionState.Disconnected || _reconnectCancellation is null || _reconnectCancellation.IsCancellationRequested)
            return;
        if (Interlocked.CompareExchange(ref _reconnectRunning, 1, 0) != 0)
            return;
        _ = ReconnectLoopAsync(_reconnectCancellation.Token, failure);
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken, Exception? failure)
    {
        try
        {
            SetState(ConnectionState.Reconnecting);
            Notice?.Invoke(failure is null ? "连接已断开，准备重新连接。" : $"连接已断开：{failure.Message}");
            foreach (var seconds in new[] { 1, 2, 5, 10 })
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
                var settings = _settings;
                if (settings is null)
                    return;
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await CloseClientAsync().ConfigureAwait(false);
                    var summary = await OpenSessionAsync(settings, isReconnect: true, cancellationToken).ConfigureAwait(false);
                    _currentDevice = summary;
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    Notice?.Invoke($"重新连接失败：{ex.Message}；稍后重试。");
                }
                finally
                {
                    _gate.Release();
                }
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                var settings = _settings;
                if (settings is null)
                    return;
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await CloseClientAsync().ConfigureAwait(false);
                    var summary = await OpenSessionAsync(settings, isReconnect: true, cancellationToken).ConfigureAwait(false);
                    _currentDevice = summary;
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    Notice?.Invoke($"重新连接失败：{ex.Message}；10 秒后重试。");
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            Interlocked.Exchange(ref _reconnectRunning, 0);
            if (!cancellationToken.IsCancellationRequested && _state == ConnectionState.Reconnecting && _client is null)
                SetState(ConnectionState.Faulted);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _reconnectCancellation?.Cancel();
        _heartbeatCancellation?.Cancel();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _settings = null;
            await CloseClientAsync().ConfigureAwait(false);
            _currentDevice = null;
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CloseClientAsync()
    {
        var old = _client;
        _client = null;
        await StopLeaseRenewalAsync().ConfigureAwait(false);
        var lease = _activeLease;
        if (old is not null)
        {
            _heartbeatCancellation?.Cancel();
            if (_heartbeatTask is not null)
            {
                try { await _heartbeatTask.ConfigureAwait(false); }
                catch { }
                _heartbeatTask = null;
            }
            if (old.HasSession && _state == ConnectionState.Connected)
            {
                if (_currentDevice is { } device
                    && device.SupportedMethods.Contains("motion.stop_all")
                    && device.Role is "operator" or "factory" or "engineer")
                {
                    try
                    {
                        await RpcAsync(old, "motion.stop_all", new JsonObject
                        {
                            ["reason"] = "workbench_disconnect"
                        }, CancellationToken.None, null, timeout: TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    catch
                    {
                        Notice?.Invoke("断开前的立即停止请求未确认；设备断线策略仍需负责停止运动，请现场核查。");
                    }
                }
                if (lease is not null)
                {
                    try
                    {
                        await RpcAsync(old, "control.release", new JsonObject
                        {
                            ["lease_id"] = lease.LeaseId.ToString()
                        }, CancellationToken.None, lease, timeout: TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    catch { /* Disconnecting the session still lets the device revoke the lease. */ }
                }
                try
                {
                    await old.RequestAsync("session.close", new JsonObject(), timeout: TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch { /* Closing the socket still invokes the device disconnect policy. */ }
            }
            await old.DisposeAsync().ConfigureAwait(false);
        }
        if (lease is not null)
            ClearControlLease();
    }

    private void SetState(ConnectionState state)
    {
        _state = state;
        StateChanged?.Invoke(state);
    }

    private static Guid ReadGuid(JsonElement element, string property) =>
        Guid.TryParse(GetStringOrNull(element, property), out var value)
            ? value
            : throw new ProtocolException($"session.open returned an invalid {property}.");

    private static bool TryGetInt32Value(JsonElement element, out int result)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out result))
            return true;
        if (element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            return true;
        result = 0;
        return false;
    }

    private static string? GetStringOrNull(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetStringOrNull(JsonElement? element, string property) =>
        element is { } value ? GetStringOrNull(value, property) : null;

    private static HashSet<string> ReadStringSet(JsonElement element, string property) =>
        new(ReadStringArray(element, property), StringComparer.Ordinal);

    private static string[] ReadStringArray(JsonElement element, string property) =>
        element.TryGetProperty(property, out var values) ? ReadStringArray(values) : [];

    private static string[] ReadStringArray(JsonElement values) =>
        values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!).Distinct(StringComparer.Ordinal).ToArray()
            : [];

    private static IReadOnlyList<string> BuildCapabilityNotes(JsonElement capabilities)
    {
        var notes = new List<string>();
        if (capabilities.TryGetProperty("axes", out var axes) && axes.ValueKind == JsonValueKind.Object)
        {
            foreach (var (axisName, label) in new[] { ("zoom", "ZOOM"), ("focus", "FOCUS") })
            {
                if (!axes.TryGetProperty(axisName, out var axis) || axis.ValueKind != JsonValueKind.Object)
                    continue;
                var rangeValid = ReadBooleanOrNull(axis, "range_valid");
                if (rangeValid == true
                    && axis.TryGetProperty("min_position", out var minimum)
                    && axis.TryGetProperty("max_position", out var maximum)
                    && minimum.ValueKind == JsonValueKind.Number
                    && maximum.ValueKind == JsonValueKind.Number)
                    notes.Add($"{label} 原始行程 {minimum.GetRawText()}–{maximum.GetRawText()}");
                else if (rangeValid == false)
                    notes.Add($"{label} 原始行程未知");

                if (ReadBooleanOrNull(axis, "optical_mapping_valid") == false)
                    notes.Add($"{label} 光学映射未验证，只显示 raw 位置");
            }
        }

        foreach (var (property, label) in new[]
        {
            ("simultaneous_motion", "双轴同时运动"),
            ("inflight_target_update", "运动中更新目标"),
            ("zoom_tracking", "变焦随动")
        })
            if (GetStringOrNull(capabilities, property) == "unverified")
                notes.Add($"{label}未验证");

        foreach (var (property, label) in new[]
        {
            ("frame_sampling", "帧采样"),
            ("roi", "ROI"),
            ("preview", "视频预览"),
            ("model_import", "模型导入"),
            ("temperature_compensation", "温度补偿")
        })
            if (ReadBooleanOrNull(capabilities, property) == false)
                notes.Add($"{label}未开放");

        return notes;
    }

    private static bool AreAxisRangesValid(JsonElement capabilities)
    {
        if (!capabilities.TryGetProperty("axes", out var axes) || axes.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var axisName in new[] { "zoom", "focus" })
        {
            if (!axes.TryGetProperty(axisName, out var axis) || axis.ValueKind != JsonValueKind.Object
                || ReadBooleanOrNull(axis, "range_valid") != true
                || !axis.TryGetProperty("min_position", out var minimum)
                || !axis.TryGetProperty("max_position", out var maximum)
                || minimum.ValueKind != JsonValueKind.Number
                || maximum.ValueKind != JsonValueKind.Number)
                return false;
        }
        return true;
    }

    private static bool? ReadBooleanOrNull(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private void PublishSnapshot(bool isFreshDeviceSnapshot)
    {
        var snapshot = _stateStore.GetSnapshot();
        if (snapshot is null)
            return;
        using var document = JsonDocument.Parse(snapshot.ToJsonString());
        SnapshotUpdated?.Invoke(document.RootElement.Clone(), isFreshDeviceSnapshot);
    }

    private static BigInteger ParseUInt64String(string value)
    {
        if (!BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < 0 || result > ulong.MaxValue)
            throw new ProtocolException("64-bit sequence values must be non-negative decimal strings.");
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _reconnectCancellation?.Cancel();
        await DisconnectAsync().ConfigureAwait(false);
        _heartbeatCancellation?.Dispose();
        _reconnectCancellation?.Dispose();
        _gate.Dispose();
    }
}

public sealed class DeviceRpcException(string method, string code, string message)
    : IOException($"{method}: {code} — {message}")
{
    public string Method { get; } = method;
    public string Code { get; } = code;
}

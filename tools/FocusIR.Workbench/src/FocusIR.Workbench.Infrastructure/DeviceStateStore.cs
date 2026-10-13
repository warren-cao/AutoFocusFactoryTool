using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FocusIR.Workbench.Protocol;

namespace FocusIR.Workbench.Infrastructure;

public sealed class DeviceStateStore
{
    private readonly object _gate = new();
    private JsonObject? _snapshot;
    private JsonObject? _latestTelemetry;
    private string? _bootId;
    private BigInteger _snapshotSequence;
    private BigInteger _eventSequence;
    private BigInteger _sampleSequence;

    public string? BootId { get { lock (_gate) return _bootId; } }
    public string SnapshotSequence { get { lock (_gate) return _snapshotSequence.ToString(CultureInfo.InvariantCulture); } }

    public JsonObject? GetSnapshot()
    {
        lock (_gate)
            return _snapshot is null ? null : (JsonObject)_snapshot.DeepClone();
    }

    public JsonObject? GetLatestTelemetry()
    {
        lock (_gate)
            return _latestTelemetry is null ? null : (JsonObject)_latestTelemetry.DeepClone();
    }

    public void ReplaceSnapshot(JsonElement snapshot, string bootId)
    {
        if (snapshot.ValueKind != JsonValueKind.Object)
            throw new ProtocolException("state.get_snapshot result must be an object.");
        var parsed = JsonNode.Parse(snapshot.GetRawText()) as JsonObject ?? throw new ProtocolException("Snapshot JSON could not be read.");
        var sequence = ReadSequence(snapshot, "snapshot_seq");
        lock (_gate)
        {
            _bootId = bootId;
            _snapshot = parsed;
            _snapshotSequence = sequence;
            _eventSequence = sequence;
            _sampleSequence = BigInteger.Zero;
            _latestTelemetry = null;
        }
    }

    public EventApplyResult ApplyReliableEvent(JsonElement message)
    {
        var bootId = ReadString(message, "boot_id");
        var sequence = ReadSequence(message, "event_seq");
        var topic = ReadString(message, "topic");
        var data = message.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(dataElement.GetRawText())
            : null;

        lock (_gate)
        {
            if (_snapshot is null || bootId != _bootId)
                return EventApplyResult.BootChanged;
            if (sequence <= _eventSequence)
                return EventApplyResult.Duplicate;
            if (sequence != _eventSequence + BigInteger.One)
                return EventApplyResult.Gap;

            switch (topic.Split('.', 2)[0])
            {
                case "control":
                    _snapshot["control"] = Merge(_snapshot["control"], data);
                    if (data is JsonObject controlPatch)
                    {
                        CopyIfPresent(controlPatch, "held", "control_held", _snapshot);
                        CopyIfPresent(controlPatch, "control_epoch", _snapshot);
                    }
                    break;
                case "task": _snapshot["task"] = Merge(_snapshot["task"], CreateTaskState(message, data)); break;
                case "fault": _snapshot["fault"] = Merge(_snapshot["fault"], data); break;
                case "model":
                {
                    _snapshot["model"] = Merge(_snapshot["model"], data);
                    if (data is JsonObject modelPatch)
                    {
                        CopyIfPresent(modelPatch, "scene_state", _snapshot);
                        CopyIfPresent(modelPatch, "scene_revision", "scene_model_revision", _snapshot);
                        if (modelPatch.ContainsKey("scene_revision") || modelPatch.ContainsKey("scene_state"))
                            _snapshot["scene_model_valid"] = null;
                    }
                    break;
                }
                case "config":
                {
                    _snapshot["config"] = Merge(_snapshot["config"], data);
                    if (data is JsonObject configPatch)
                    {
                        CopyIfPresent(configPatch, "mechanical_ready", _snapshot);
                        CopyIfPresent(configPatch, "image_ready", _snapshot);
                        CopyIfPresent(configPatch, "last_error", _snapshot);
                    }
                    break;
                }
            }
            _snapshot["snapshot_seq"] = sequence.ToString(CultureInfo.InvariantCulture);
            _snapshot["last_reliable_event"] = JsonNode.Parse(message.GetRawText());
            _eventSequence = sequence;
            _snapshotSequence = sequence;
            return EventApplyResult.Applied;
        }
    }

    public bool ApplyTelemetry(JsonElement message)
    {
        var sampleSequence = ReadSequence(message, "sample_seq");
        var bootId = ReadString(message, "boot_id");
        var telemetry = JsonNode.Parse(message.GetRawText()) as JsonObject ?? throw new ProtocolException("Telemetry JSON could not be read.");
        lock (_gate)
        {
            if (_snapshot is null || bootId != _bootId)
                return false;
            if (sampleSequence <= _sampleSequence)
                return false;
            _sampleSequence = sampleSequence;
            _latestTelemetry = telemetry;
            return true;
        }
    }

    private static JsonObject CreateTaskState(JsonElement message, JsonNode? data)
    {
        var task = data is JsonObject taskObject ? (JsonObject)taskObject.DeepClone() : new JsonObject();
        if (message.TryGetProperty("task_id", out var taskId) && taskId.ValueKind == JsonValueKind.String)
            task["task_id"] = taskId.GetString();
        return task;
    }

    private static JsonObject Merge(JsonNode? current, JsonNode? patch)
    {
        var result = current is JsonObject currentObject ? (JsonObject)currentObject.DeepClone() : new JsonObject();
        if (patch is JsonObject patchObject)
            foreach (var property in patchObject)
                result[property.Key] = property.Value?.DeepClone();
        return result;
    }

    private static void CopyIfPresent(JsonObject source, string property, JsonObject destination)
    {
        if (source.TryGetPropertyValue(property, out var value))
            destination[property] = value?.DeepClone();
    }

    private static void CopyIfPresent(JsonObject source, string sourceProperty, string destinationProperty, JsonObject destination)
    {
        if (source.TryGetPropertyValue(sourceProperty, out var value))
            destination[destinationProperty] = value?.DeepClone();
    }

    private static BigInteger ReadSequence(JsonElement element, string property)
    {
        var text = ReadString(element, property);
        if (!BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0 || value > ulong.MaxValue)
            throw new ProtocolException($"{property} must be a non-negative decimal string no greater than uint64 max.");
        return value;
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : throw new ProtocolException($"{property} must be a string.");
}

public enum EventApplyResult
{
    Applied,
    Duplicate,
    Gap,
    BootChanged
}

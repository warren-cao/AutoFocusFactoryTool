using System.Globalization;
using System.Text.Json;
namespace FocusIR.Workbench.Domain;

public sealed record FactorySample(ulong Id, string NodeId, ulong FrameId, long CaptureTime,
    string TimeSource, string Stage, string Direction, double TargetZoom, double TargetFocus,
    double Zoom, double Focus, double? Score, bool Valid, string InvalidReason, ulong ZoomRxSequence, ulong FocusRxSequence);
public sealed record FactoryNode(string Id, string Kind, string Direction, double Zoom,
    double Focus, double Score, double Noise, double ClearLeft, double ClearRight, double? Repeatability,
    double PredictedFocus, double PredictedScore, double ScoreRatio, double PredictionError,
    double AllowedError, bool Passed, double IntervalLeft, double IntervalRight, ulong Generation,
    bool ClearLeftValid, bool ClearRightValid, bool BoundaryLimited, bool MeasurementValid, string FailureReason);
public sealed record FactoryCurvePoint(string Direction, double Zoom, double Focus);

public static class FactoryDataReader
{
    public static string Text(JsonElement e, string name, string fallback = "") =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : v.ToString() : fallback;
    public static double Number(JsonElement e, string name, double fallback = 0)
    {
        if (!e.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n)) return n;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && double.IsFinite(n)) return n;
        return fallback;
    }
    public static ulong UInt64(JsonElement e, string name) => ulong.TryParse(Text(e, name), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
    public static bool Boolean(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    public static IEnumerable<JsonElement> Items(JsonElement page, string name) =>
        page.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToArray() : [];
    public static FactorySample Sample(JsonElement e) => new(UInt64(e,"sample_id"),Text(e,"node_id"),UInt64(e,"frame_id"),
        (long)UInt64(e,"capture_monotonic_ms"),Text(e,"time_source"),Text(e,"stage"),Text(e,"direction"),
        Number(e,"target_zoom_position"),Number(e,"target_focus_position"),Number(e,"zoom_position"),Number(e,"focus_position"),
        e.TryGetProperty("score",out var s) && s.ValueKind != JsonValueKind.Null ? Number(e,"score",double.NaN) : null,
        Boolean(e,"valid"),Text(e,"invalid_reason"),UInt64(e,"zoom_rx_seq"),UInt64(e,"focus_rx_seq"));
    public static FactoryNode Node(JsonElement e) => new(Text(e,"node_id"),Text(e,"kind"),Text(e,"direction"),
        Number(e,"zoom_position"),Number(e,"focus_position"),Number(e,"best_score"),Number(e,"score_noise"),
        Number(e,"clear_left"),Number(e,"clear_right"),
        e.TryGetProperty("repeatability",out var repeat) && repeat.ValueKind==JsonValueKind.Null ? null : Number(e,"repeatability"),Number(e,"predicted_focus"),
        Number(e,"predicted_score"),Number(e,"score_ratio"),Number(e,"prediction_error"),Number(e,"allowed_error"),
        Boolean(e,"passed"),Number(e,"interval_left"),Number(e,"interval_right"),UInt64(e,"validation_generation"),
        !e.TryGetProperty("clear_left_valid",out _) || Boolean(e,"clear_left_valid"),
        !e.TryGetProperty("clear_right_valid",out _) || Boolean(e,"clear_right_valid"),Boolean(e,"boundary_limited"),!e.TryGetProperty("measurement_valid",out _) || Boolean(e,"measurement_valid"),Text(e,"failure_reason"));
    public static string StageLabel(string stage) => stage switch {
        "speed_calibration" => "测量双轴各档位速度", "tracking_lag" => "持续变倍，追焦偏差偏大（请人工调速）",
        "limit_hold" => "变倍已到端点，等待松开或换向", "direction_mismatch" => "电机实际方向不符", "target_overshoot" => "变倍目标超出到位容差", "manual_tracking" => "手动变倍联动", "identify_zoom_direction" => "确认变倍方向", "identify_focus_direction" => "确认聚焦方向", "queued" => "等待执行", "hardware_probe" => "硬件短行程检查", "align" => "对齐方向曲线", "initial_focus_align" => "启动前快速对焦", "manual_focus_catchup" => "持续变倍，聚焦快速追赶", "final_focus_follow" => "变倍已停止，聚焦收尾追到位", "tracking" => "正在联动", "segmented_tracking" => "低速分段联动", "zoom_no_progress" => "变倍未继续推进", "focus_no_progress" => "聚焦追赶未推进（已停止）", "focus_catchup" => "暂停变倍并追焦", "idle" => "空闲", "failed" => "已停止，任务失败",
        "continuous_candidate" => "连续粗扫", "peak_refine" => "峰顶精调", "fitted_focus" => "拟合焦点复测",
        "final_confirm" => "最佳焦点确认", "clear_boundary" => "95% 清晰边界", "return_verify" => "独立回位复验", "prediction_verify" => "预测位置评分", "add_node" => "补点",
        "stationary_coarse" => "停稳粗采", "anchor" => "等待有效新帧", "restore" => "恢复起始位置", "completed" => "已完成并提交模型", "light_check" => "轻量检查", "interval_validation" => "曲线检查", _ => stage };
}

using System.Globalization;
using System.Text.Json;
using System.Windows;
using FocusIR.Workbench.Domain;

namespace FocusIR.Workbench.App.ViewModels;

public sealed partial class MainViewModel
{
    private JsonElement? _autofocusConfiguration;
    private string _autofocusSlowScore = "200";
    private string _autofocusConfigurationStatus = "尚未读取设备 AF 设置。";
    private bool _autofocusConfigurationDirty, _autofocusConfigurationReading;
    private long _autofocusConfigurationLastReadTick;
    private int _autofocusConfigurationGeneration;

    public string AutofocusSlowScore
    {
        get => _autofocusSlowScore;
        set
        {
            if (_autofocusSlowScore == value) return;
            _autofocusSlowScore = value;
            _autofocusConfigurationDirty = true;
            OnPropertyChanged();
            RefreshAutofocusConfigurationProperties();
        }
    }
    public string AutofocusConfigurationStatus => _autofocusConfigurationStatus
        + (_autofocusConfigurationDirty ? TryParseAutofocusSlowScore(out _) ? " 当前输入尚未保存。" : " 请输入大于 0 且不超过 1000000 的有效数值。" : "");
    public Visibility AutofocusConfigurationVisibility => OperationControlsVisibility == Visibility.Visible
        && Supports("af.get_config") ? Visibility.Visible : Visibility.Collapsed;
    public bool CanRefreshAutofocusConfiguration => !_isBusy && !_operationBusy && !_autofocusConfigurationReading
        && _state == ConnectionState.Connected && Supports("af.get_config");
    public bool CanEditAutofocusConfiguration => !_isBusy && !_operationBusy && !_factoryTaskFinalizing
        && !Charts.IsEditing && _state == ConnectionState.Connected && NoActiveTaskConfirmed()
        && Supports("af.set_config") && (_device?.Role is "factory" or "engineer");
    public bool CanSaveAutofocusConfiguration => CanEditAutofocusConfiguration && !_autofocusConfigurationReading
        && _autofocusConfiguration is not null && (_lease is null || _lease.Purpose is "operation" or "factory")
        && TryParseAutofocusSlowScore(out _) && !_uncertainRequestMethods.Values.Contains("af.set_config");

    private bool TryParseAutofocusSlowScore(out double value) => double.TryParse(AutofocusSlowScore,
        NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value > 0 && value <= 1000000;

    private void RefreshAutofocusConfigurationProperties()
    {
        OnPropertyChanged(nameof(AutofocusConfigurationVisibility));
        OnPropertyChanged(nameof(AutofocusConfigurationStatus));
        OnPropertyChanged(nameof(CanRefreshAutofocusConfiguration));
        OnPropertyChanged(nameof(CanEditAutofocusConfiguration));
        OnPropertyChanged(nameof(CanSaveAutofocusConfiguration));
    }

    private void ClearAutofocusConfiguration()
    {
        ++_autofocusConfigurationGeneration;
        _autofocusConfiguration = null;
        _autofocusConfigurationReading = false;
        _autofocusConfigurationLastReadTick = 0;
        _autofocusConfigurationDirty = false;
        _autofocusSlowScore = "200";
        _autofocusConfigurationStatus = "尚未读取设备 AF 设置。";
        OnPropertyChanged(nameof(AutofocusSlowScore));
        RefreshAutofocusConfigurationProperties();
    }

    private void ApplyAutofocusConfiguration(JsonElement configuration, bool loadInput)
    {
        var revision = ReadString(configuration, "revision");
        if (!ulong.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out var revisionNumber)
            || revision != revisionNumber.ToString(CultureInfo.InvariantCulture)
            || !configuration.TryGetProperty("slow_score", out var scoreElement)
            || scoreElement.ValueKind != JsonValueKind.Number || !scoreElement.TryGetDouble(out var score)
            || !double.IsFinite(score) || score <= 0 || score > 1000000)
            throw new InvalidOperationException("设备返回的 AF 阈值或配置版本无效，请刷新后确认。");
        _autofocusConfiguration = configuration.Clone();
        if (loadInput || !_autofocusConfigurationDirty)
        {
            _autofocusSlowScore = score.ToString("G", CultureInfo.InvariantCulture);
            _autofocusConfigurationDirty = false;
            OnPropertyChanged(nameof(AutofocusSlowScore));
        }
        _autofocusConfigurationStatus = $"设备已保存减速阈值 {score:G} · 版本 {revision}。";
        RefreshAutofocusConfigurationProperties();
    }

    private async Task ReadAutofocusConfigurationCoreAsync(bool loadInput = false)
    {
        if (_autofocusConfigurationReading || _state != ConnectionState.Connected || !Supports("af.get_config")) return;
        var generation = _autofocusConfigurationGeneration;
        var bootId = _device?.BootId;
        _autofocusConfigurationReading = true;
        RefreshAutofocusConfigurationProperties();
        try
        {
            var result = await _connection.GetAutofocusConfigurationAsync();
            if (generation == _autofocusConfigurationGeneration && _state == ConnectionState.Connected && _device?.BootId == bootId)
                ApplyAutofocusConfiguration(result, loadInput);
        }
        finally
        {
            if (generation == _autofocusConfigurationGeneration)
            {
                _autofocusConfigurationReading = false;
                _autofocusConfigurationLastReadTick = Environment.TickCount64;
                RefreshAutofocusConfigurationProperties();
            }
        }
    }

    private void QueueAutofocusConfigurationRefresh(bool force = false)
    {
        if (_autofocusConfigurationReading || _operationBusy || _state != ConnectionState.Connected || !Supports("af.get_config")) return;
        if (!force && _autofocusConfigurationLastReadTick != 0 && Environment.TickCount64 - _autofocusConfigurationLastReadTick < 5000) return;
        _ = RefreshAutofocusConfigurationInBackgroundAsync(_autofocusConfigurationGeneration);
    }

    private async Task RefreshAutofocusConfigurationInBackgroundAsync(int generation)
    {
        try { await ReadAutofocusConfigurationCoreAsync(); }
        catch (Exception ex)
        {
            if (generation != _autofocusConfigurationGeneration || _state != ConnectionState.Connected) return;
            _autofocusConfigurationStatus = $"AF 设置回读失败：{ex.Message}；请点击读取 AF 设置重试。";
            RefreshAutofocusConfigurationProperties();
        }
    }

    public async Task RefreshAutofocusConfigurationAsync()
    {
        if (!CanRefreshAutofocusConfiguration) return;
        await RunOperationAsync(async () =>
        {
            await ReadAutofocusConfigurationCoreAsync(loadInput: true);
            _statusNotice = AutofocusConfigurationStatus;
            AddActivity(_statusNotice);
        });
    }

    public async Task SaveAutofocusConfigurationAsync()
    {
        if (!CanSaveAutofocusConfiguration || !TryParseAutofocusSlowScore(out var slowScore)
            || _autofocusConfiguration is not { } configuration) return;
        var revision = ReadString(configuration, "revision") ?? throw new InvalidOperationException("请先读取设备 AF 配置版本。");
        await RunOperationAsync(async () =>
        {
            bool temporaryLease = false;
            try
            {
                await _connection.RefreshSnapshotAsync();
                if (!NoActiveTaskConfirmed() || HasActiveTask())
                    throw new InvalidOperationException("设备有活动任务，不能修改自动聚焦阈值。");
                if (_lease?.Purpose == "operation")
                {
                    await _connection.ReleaseControlAsync();
                    _lease = null;
                }
                if (_lease is null)
                {
                    _lease = await _connection.AcquireControlAsync("factory");
                    temporaryLease = true;
                }
                if (_lease?.Purpose != "factory")
                    throw new InvalidOperationException("当前控制权用途不允许保存 AF 设置，请先释放控制权。");
                await _connection.RefreshSnapshotAsync();
                if (!NoActiveTaskConfirmed() || HasActiveTask())
                    throw new InvalidOperationException("设备已有活动任务，AF 设置未保存。");
                var result = await _connection.SetAutofocusConfigurationAsync(slowScore, revision);
                ApplyAutofocusConfiguration(result, loadInput: true);
                _statusNotice = $"AF 减速阈值 {slowScore:G} 已保存到设备，后续普通自动聚焦使用此设置。";
                AddActivity(_statusNotice);
            }
            catch (Exception ex)
            {
                _autofocusConfigurationStatus = $"AF 设置未确认保存：{ex.Message}；请回读设备设置确认。";
                RefreshAutofocusConfigurationProperties();
                throw;
            }
            finally
            {
                if (temporaryLease)
                {
                    try { await _connection.ReleaseControlAsync(); _lease = null; }
                    catch (Exception ex) { AddActivity($"AF 设置操作结束，控制权释放未确认：{ex.Message}"); }
                }
            }
        });
    }
}

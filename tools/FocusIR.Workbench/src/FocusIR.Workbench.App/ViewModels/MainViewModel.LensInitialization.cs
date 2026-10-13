using System.Text.Json;
using FocusIR.Workbench.Domain;
using FocusIR.Workbench.Protocol;
namespace FocusIR.Workbench.App.ViewModels;

public sealed partial class MainViewModel
{
    private string _activeLensModel = "", _activeProfileId = "";
    private string _activeLensStatus = "设备尚未激活镜头型号。";
    private string _lensInitializationStatus = "先激活型号，再将目标置于画面中央，点击镜头初始化。";
    private string _lensBudgetSeconds = "180";
    private string? _lensTaskId, _lensTaskModel;
    public string ActiveLensStatus => _activeLensStatus;
    public string LensInitializationStatus => _lensInitializationStatus;
    private bool SelectedLensIsActive => SelectedLensModel is { } lens && lens.Model == _activeLensModel;
    public string LensBudgetSeconds
    {
        get => _lensBudgetSeconds;
        set { _lensBudgetSeconds = value; OnPropertyChanged(); RefreshLensProperties(); }
    }
    private bool CanStartLensTask => CanBrowseProfiles && !_profileLoading && _lease is null
        && NoActiveTaskConfirmed() && SelectedLensModel is not null && FactoryControlsVisibility == System.Windows.Visibility.Visible;
    public bool CanActivateLensModel => CanStartLensTask && Supports("lens.activate");
    public bool CanInitializeLens => CanStartLensTask && SelectedLensIsActive && Supports("lens.initialize")
        && FactoryHardwareReady() && Profiles.Any(p => p.HasModel)
        && int.TryParse(LensBudgetSeconds, out var seconds) && seconds is >= 1 and <= 600;
    private void RefreshLensProperties()
    {
        OnPropertyChanged(nameof(ActiveLensStatus)); OnPropertyChanged(nameof(LensInitializationStatus));
        OnPropertyChanged(nameof(CanActivateLensModel)); OnPropertyChanged(nameof(CanInitializeLens));
    }
    private void ApplyActiveLensState(JsonElement page)
    {
        _activeLensModel = FactoryDataReader.Text(page,"active_lens_model"); _activeProfileId = FactoryDataReader.Text(page,"active_profile_id");
        _activeLensStatus = _activeLensModel.Length==0 ? "设备尚未激活镜头型号。"
            : _activeProfileId.Length==0 ? $"当前型号：{_activeLensModel} · 等待选择物距"
            : $"当前型号：{_activeLensModel} · 物距：{FactoryDataReader.Number(page,"active_distance_m"):0.###} m";
        RefreshLensProperties();
    }
    public async Task StartLensTaskAsync(bool initialize)
    {
        if(!(initialize?CanInitializeLens:CanActivateLensModel) || SelectedLensModel is not { } lens) return;
        int seconds=int.TryParse(LensBudgetSeconds,out var parsed)?parsed:180;
        await RunOperationAsync(async () =>
        {
            _lease=await _connection.AcquireControlAsync(initialize?"scene_init":"factory");
            try
            {
                await _connection.RefreshSnapshotAndResubscribeAsync();
                if(HasActiveTask() || !NoActiveTaskConfirmed() || initialize && !FactoryHardwareReady())
                    throw new InvalidOperationException("设备有活动任务或未就绪，请确认后重试。");
                _lensTaskModel=lens.Model;
                var result=await _connection.StartLensTaskAsync(lens.Model,initialize,initialize?seconds*1000:30000);
                _lastTaskId=ReadString(result,"task_id")??throw new InvalidOperationException("设备未返回任务编号，请查询请求状态确认结果。");
                _lastTaskKind="lens";_latestTaskStatus=result.Clone();_lensTaskId=_lastTaskId;_lensTaskModel=lens.Model;_factoryResult=null;
                _lensInitializationStatus=initialize?$"正在初始化 {lens.Model}，总预算 {seconds} 秒；请保持中心目标稳定。"
                    :$"正在激活 {lens.Model} 并恢复上次有效物距；此操作不移动电机。";
                _statusNotice=_lensInitializationStatus;AddActivity(_statusNotice);RefreshLensProperties();StartTaskMonitor(_lastTaskId,"lens");
            }
            catch(RequestTimeoutException)
            {
                _lastTaskKind="lens";
                throw; // 原请求是否被接受未知；继续续租并由请求状态查询接回任务。
            }
            catch
            {
                if(!HasActiveTask()) {await _connection.ReleaseControlAsync();_lease=null;}
                throw;
            }
        });
    }
    private void ApplyLensTaskProgress(JsonElement task)
    {
        var stage=LensStageText(FactoryDataReader.Text(task,"stage"));
        _lensInitializationStatus=$"{stage} · 已探测 {FactoryDataReader.Number(task,"completed_nodes"):0} 个倍率 · 已用 {FactoryDataReader.Number(task,"elapsed_ms")/1000:0.0} 秒";
        RefreshLensProperties();
    }
    private static string LensStageText(string stage)=>stage switch
    {
        "lens_score_profiles"=>"采集候选物距清晰度评分", "lens_full_sweep"=>"全行程参考扫焦", "lens_stationary_refinement"=>"停稳新帧精修", "lens_peak_extension"=>"补充峰值边缘精修点", "lens_compare_profiles"=>"比较物距配置",
        "lens_confirm_profile"=>"确认候选物距清晰度", "lens_restore_and_focus"=>"返回原倍率并合焦", "lens_restore_original"=>"恢复原位置",
        "lens_motion_retry"=>"读取最新反馈并重新定位", "lens_expand_search"=>"扩大聚焦搜索范围", "lens_final_focus_retry"=>"重新搜索原倍率焦点",
        _=>"准备或切换配置"
    };
    private async Task FinishLensTaskAsync(string taskId,JsonElement task,CancellationToken cancellationToken)
    {
        var model=_lensTaskModel;string message;
        if(task.TryGetProperty("lens_result",out var report))
        {
            var conclusion=FactoryDataReader.Text(report,"conclusion");
            var returnAdjusted=FactoryDataReader.Boolean(report,"return_zoom_adjusted");
            message=conclusion switch
            {
                "score_selected"=>returnAdjusted?"镜头初始化完成，已按最高实测评分激活物距并定位到最近有效倍率。":"镜头初始化完成，已按最高实测评分激活物距并恢复原倍率。",
                "score_selected_partial"=>returnAdjusted?"最高评分配置已激活并定位到最近有效倍率；局部寻焦采用已测最佳点，未完成完整峰形确认。":"最高评分配置已激活并返回原倍率；局部寻焦采用已测最佳点，未完成完整峰形确认。",
                "matched"=>returnAdjusted?"镜头初始化成功，已激活匹配物距并在最近有效倍率合焦。":"镜头初始化成功，已激活匹配物距并返回原倍率合焦。",
                "budget_insufficient"=>"预算不足以完成一组候选评分和回位，本次未启动电机。",
                "restored_previous_profile"=>"镜头型号已激活，已恢复上次有效物距。",
                "awaiting_profile"=>"镜头型号已激活，暂无有效物距；请标定或选择物距。",
                "ambiguous"=>"物距无法可靠区分，保留原配置。", "budget_exceeded"=>"初始化预算不足或已用尽，保留原配置。",
                "cancelled"=>"初始化已取消，未提交新物距。",
                "sampling_failed"=>"停稳采样失败，未提交新物距，保留原配置。",
                "motion_failed"=>"电机定位或反馈失败，未提交新物距。",
                "peak_unreliable"=>"本倍率未取得可靠焦点，保留原配置。",
                "not_matched"=>LensTaskFailureSummary(task),
                _=>"镜头初始化失败，保留原配置。"
            };
            message+=$" 耗时 {FactoryDataReader.Number(report,"elapsed_ms")/1000:0.0} 秒。";
            if(FactoryDataReader.Text(task,"operation")=="lens.initialize")
                message+=FactoryDataReader.Boolean(report,"restore_confirmed")
                    ?returnAdjusted?$" 已定位到最近有效倍率（目标 {FactoryDataReader.Number(report,"return_zoom"):0}，原位置 {FactoryDataReader.Number(report,"requested_zoom"):0}，实际 {FactoryDataReader.Number(report,"actual_return_zoom"):0}）。":" 已确认返回原倍率。"
                    :report.TryGetProperty("movement_attempted",out _) && !FactoryDataReader.Boolean(report,"movement_attempted")?" 本次未开始电机运动。"
                    :" 未确认返回原位，请检查设备位置。";
            var ranking=string.Join(" → ",FactoryDataReader.Items(report,"candidates").Select(c=>$"{FactoryDataReader.Number(c,"target_distance_m"):0.###} m"
                +(c.TryGetProperty("score",out _)?$"（评分 {FactoryDataReader.Number(c,"score"):0.00}）"
                    :c.TryGetProperty("mean_error",out _)?$"（偏差 {FactoryDataReader.Number(c,"mean_error"):0.00}）":"（尚未评分）")));
            if(ranking.Length>0) message+="\n候选排序："+ranking;
            if(conclusion is "score_selected" or "score_selected_partial")
                message+=$"\n最高平均实测评分 {FactoryDataReader.Number(report,"selected_score"):0.00}，完成 {FactoryDataReader.Number(report,"completed_scoring_zooms"):0} 个倍率的候选评分。";
            var reason=FactoryDataReader.Text(report,"reason");
            var warning=FactoryDataReader.Text(report,"warning");
            if(warning.Length>0 && warning!=reason) message+="\n说明："+LensFailureText(warning);
            var warnings=FactoryDataReader.Items(report,"warnings").Where(w=>w.ValueKind==JsonValueKind.String)
                .Select(w=>w.GetString()??"").Where(w=>w.Length>0 && w!=warning && w!=reason).Distinct().Select(LensFailureText).Distinct().ToArray();
            if(warnings.Length>0) message+="\n恢复说明："+string.Join("；",warnings);
            if(reason.Length>0) message+="\n原因："+LensFailureText(reason);
            else if(FactoryDataReader.Text(task,"state")=="FAILED")
            {
                var detail=FactoryDataReader.Text(task,"error_message");
                if(detail.Length>0) message+="\n原因："+LensFailureText(detail);
                else if(FactoryDataReader.Text(task,"error_code") is { Length: > 0 } code) message+="\n原因："+LensFailureText(code);
            }
            if(FactoryDataReader.Text(report,"conclusion")=="budget_insufficient" && report.TryGetProperty("budget",out var budget))
                message+=$" 当前预算 {FactoryDataReader.Number(budget,"requested_ms")/1000:0.0} 秒，完成评分及回位至少需约 {FactoryDataReader.Number(budget,"minimum_ms")/1000:0.0} 秒。";
            if(report.TryGetProperty("sample_failure",out var sample)) {
                var stage=LensStageText(FactoryDataReader.Text(sample,"stage"));
                message+=$"\n{stage}：目标 ZOOM {FactoryDataReader.Number(sample,"target_zoom"):0} / FOCUS {FactoryDataReader.Number(sample,"target_focus"):0}"
                    +$"，有效帧 {FactoryDataReader.Number(sample,"valid_frames"):0}/{FactoryDataReader.Number(sample,"required_frames"):0}"
                    +$"，已重采 {FactoryDataReader.Number(sample,"restarts"):0} 次。";
                if(sample.TryGetProperty("frame_id",out _)) message+=$" 失败帧 {FactoryDataReader.Number(sample,"frame_id"):0}。";
                if(FactoryDataReader.Boolean(sample,"position_known")) message+=$" 实际 ZOOM {FactoryDataReader.Number(sample,"actual_zoom"):0} / FOCUS {FactoryDataReader.Number(sample,"actual_focus"):0}。";
                message+=$" 批次位置范围：ZOOM {FactoryDataReader.Number(sample,"zoom_span"):0} / FOCUS {FactoryDataReader.Number(sample,"focus_span"):0} raw，容差 {FactoryDataReader.Number(sample,"tolerance"):0} raw。";
                var source=FactoryDataReader.Text(sample,"source_reason");
                if(source.StartsWith("POSITION_CHANGED",StringComparison.Ordinal))
                    message+=$" 取帧前后变化：ZOOM {ReadDiagnosticField(source,"z_delta")??"未知"} / FOCUS {ReadDiagnosticField(source,"f_delta")??"未知"} raw。";
            }
            if(report.TryGetProperty("peak_failure",out var peak) || report.TryGetProperty("peak_warning",out peak)) {
                var points=FactoryDataReader.Items(peak,"points").ToArray();
                message+=$"\n峰值诊断：倍率 {FactoryDataReader.Number(peak,"zoom"):0}，精修步长 {FactoryDataReader.Number(peak,"step"):0} raw，已补点 {FactoryDataReader.Number(peak,"extensions"):0} 次。";
                if(points.Length>0) {
                    var best=points.OrderByDescending(x=>FactoryDataReader.Number(x,"score")).First();
                    message+=$" 精修目标范围 {FactoryDataReader.Number(points[0],"target_focus"):0}～{FactoryDataReader.Number(points[^1],"target_focus"):0} raw"
                        +$"，最高评分 {FactoryDataReader.Number(best,"score"):0.00}，实际焦点 {FactoryDataReader.Number(best,"actual_focus_min"):0}～{FactoryDataReader.Number(best,"actual_focus_max"):0} raw。";
                }
            }
            if(report.TryGetProperty("sweep_policy",out var policy) && FactoryDataReader.Text(task,"operation")=="lens.initialize")
                message+=$"\n扫焦调速：评分达到 {FactoryDataReader.Number(policy,"slow_score"):0} 时降至 {FactoryDataReader.Number(policy,"slow_gear"):0} 档，最高允许 {FactoryDataReader.Number(policy,"maximum_gear"):0} 档。";
            var excluded=FactoryDataReader.Items(report,"excluded_profiles").Select(p=>$"{FactoryDataReader.Text(p,"profile_id")}：{LensFailureText(FactoryDataReader.Text(p,"reason"))}").ToArray();
            if(excluded.Length>0) message+="\n跳过配置："+string.Join("；",excluded);
            var restoreError=FactoryDataReader.Text(report,"restore_error");
            if(restoreError.Length>0) message+="\n回位结果："+LensFailureText(restoreError);
            if(report.TryGetProperty("restore_failure",out var restore)) message+=" "+LensMotionFailureText(restore);
            if(report.TryGetProperty("motion_failure",out var motion)) message+="\n定位诊断："+LensMotionFailureText(motion);
            var retries=FactoryDataReader.Number(report,"sample_restarts");
            if(retries>0 && !report.TryGetProperty("sample_failure",out _)) message+=$"\n本次累计重采 {retries:0} 批。";
            var motionRetries=FactoryDataReader.Number(report,"motion_retries");
            var expansions=FactoryDataReader.Number(report,"search_expansions");
            if(motionRetries>0 || expansions>0) message+=$"\n本次重新定位 {motionRetries:0} 次，扩大搜索 {expansions:0} 次。";
            if(returnAdjusted || FactoryDataReader.Boolean(report,"zoom_endpoint_clamped") || FactoryDataReader.Boolean(report,"focus_endpoint_clamped"))
                message+="\n原位或端点目标已按设备最新有效行程修正。";
        }
        else message=$"镜头任务结束：{FactoryDataReader.Text(task,"state")}。{FactoryDataReader.Text(task,"error_message")}";
        try {await _connection.ReleaseControlAsync(cancellationToken).ConfigureAwait(false);}
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {return;}
        catch(Exception ex) {message+="\n控制权释放结果："+ex.Message;}
        OnUi(()=>{_lease=null;});
        try
        {
            var lenses=await _connection.ListLensModelsAsync(cancellationToken).ConfigureAwait(false);
            OnUi(()=>{if(_lastTaskId==taskId) ApplyLensModelList(lenses,model);});
            if(model is not null) await RefreshProfilesAfterTaskAsync(model,_activeProfileId.Length>0?_activeProfileId:null,cancellationToken).ConfigureAwait(false);
            if(Supports("tracking.get")) {var t=await _connection.GetTrackingAsync(cancellationToken).ConfigureAwait(false);OnUi(()=>ApplyTrackingState(t,true));}
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {return;}
        catch(Exception ex) {message+="\n配置回读失败，请刷新确认："+ex.Message;}
        OnUi(()=>{if(_lastTaskId!=taskId)return;_lensInitializationStatus=_statusNotice=message;AddActivity(message);OnPropertyChanged(nameof(StatusNotice));RefreshOperationProperties();});
    }
    private static string LensMotionFailureText(JsonElement motion) {
        var message=$"{FactoryDataReader.Text(motion,"axis")} 目标 {FactoryDataReader.Number(motion,"target"):0}";
        message+=FactoryDataReader.Boolean(motion,"position_known")?$"，实际 {FactoryDataReader.Number(motion,"actual"):0}":"，实际位置未知";
        return message+$"，设备错误码 {FactoryDataReader.Number(motion,"error_code"):0}。";
    }
    private static string LensTaskFailureSummary(JsonElement task)=>FactoryDataReader.Text(task,"error_code") switch
    {
        "OUT_OF_RANGE"=>"定位目标超出设备当前有效行程，未提交新物距。",
        "MODEL_INVALID"=>"原倍率聚焦或配置检查未完成，未提交新物距。",
        "NO_RELIABLE_PEAK"=>"扩大搜索后仍未取得可靠焦点，未提交新物距。",
        _=>"现场聚焦或清晰度确认未完成，未提交新物距。"
    };
    private static string LensFailureText(string reason)=>reason.StartsWith("POSITION_CHANGED",StringComparison.Ordinal)
        ? "取帧前后位置变化超过采样噪声容差。" : reason switch
    {
        "SAMPLE_BATCH_UNSTABLE"=>"整批位置范围超过容差，有限重采后仍未稳定。",
        "SAMPLE_POSITION_CHANGED"=>"取帧期间位置变化过大，重采未能取得稳定批次。",
        "SAMPLE_STABILITY_TIMEOUT"=>"单个位置最多重采2次、最长等待5秒后仍未取得完整稳定批次。",
        "SAMPLE_ZOOM_OFF_TARGET"=>"倍率反馈超出目标到位容差。",
        "SAMPLE_FOCUS_OFF_TARGET"=>"聚焦反馈超出目标到位容差。",
        "SAMPLE_SOURCE_OR_POSITION_LOST"=>"图像采样或位置反馈失效，已停止后续探测。",
        "SAMPLE_FRAME_UNAVAILABLE"=>"未能取得有效新图像帧。",
        "SAMPLE_NEW_FRAME_TIMEOUT"=>"等待新图像帧超时。",
        "SAMPLE_SCORE_NOT_FINITE"=>"清晰度评分无效。",
        "SAMPLE_INVALID"=>"停稳采样未通过有效性检查。",
        "SAMPLE_DEADLINE_EXCEEDED"=>"采样达到本阶段预算截止时间。",
        "SAMPLE_CANCELLED"=>"采样已取消。",
        "LENS_MOTION_FAILED"=>"电机定位未完成，详见定位诊断。",
        "OUT_OF_RANGE"=>"目标超出设备实时有效行程，需查看端点及定位诊断。",
        "MODEL_INVALID"=>"配置或原倍率焦点检查未完成，需查看搜索及回位诊断。",
        "MOTION_FAILED"=>"电机定位或反馈未完成，需查看定位诊断。",
        "ORIGINAL_ENDPOINT_CLAMPED"=>"原位置回读超出当前有效行程，已按实时行程修正返回目标。",
        "ZOOM_TARGET_CLAMPED"=>"倍率目标已按设备最新有效行程修正。",
        "FOCUS_TARGET_CLAMPED"=>"聚焦目标已按设备最新有效行程修正。",
        "MOTION_FEEDBACK_RECOVERED"=>"初次定位反馈异常后已重新读取并恢复有效回报。",
        "MOTION_RETRY_ABSOLUTE"=>"初次定位未到位，已停稳后重新发出目标定位。",
        "FINAL_SEARCH_BUDGET_FALLBACK"=>"剩余预算不足以完成完整局部寻焦，采用已采样的最佳焦点。",
        "FINAL_REFERENCE_BEST_AVAILABLE"=>"全行程参考搜索使用已测最高评分有效点，未完成完整峰形确认。",
        "FINAL_FOCUS_BEST_AVAILABLE"=>"局部搜索使用已测最高评分有效点，未完成完整峰形确认。",
        "OPTIONAL_REFERENCE_RECOVERED"=>"参考扫焦未完成完整确认，继续使用已完成的候选实测评分。",
        "NO_FINAL_FOCUS_SAMPLE"=>"原倍率没有取得有效聚焦采样，未提交新物距。",
        "RETURN_ZOOM_MODEL_CLAMPED"=>"原倍率位于有效行程或所选物距曲线之外，已改为最近有效倍率。",
        "RETURN_ZOOM_PREDICTION_FAILED"=>"最近有效倍率的聚焦预测无效，未提交新物距。",
        "RETURN_DOMAIN_EMPTY"=>"设备实时倍率行程与所选物距曲线没有有效交集。",
        "ORIGINAL_POSITION_INVALID"=>"启动时原位置反馈无效，无法确认返回目标。",
        "MOTION_RECOVERY_EXHAUSTED"=>"读取最新反馈并重新定位后仍未完成运动，详见定位诊断。",
        "RESTORE_POSITION_FAILED"=>"恢复原位置未完成，请检查电机反馈。",
        "RESTORE_SKIPPED_CANCELLED"=>"取消或控制权丢失后停止运动，未强制回位。",
        "RESTORE_SKIPPED_BUDGET"=>"总预算已用尽，未继续发出回位运动。",
        "RESTORE_SKIPPED_MOTION_FAULT"=>"运动或反馈故障，停止后续运动以避免错误回位。",
        "OPTIONAL_REFERENCE_BUDGET"=>"参考探测时间已用完，使用已完成倍率的完整候选评分选择最高分。",
        "SECOND_SCORING_RECOVERED"=>"第二倍率未取得稳定评分，已舍弃该组，继续采用第一组完整候选评分。",
        "SECOND_SCORING_SKIPPED_BUDGET"=>"剩余探测时间不足以完成第二组公平评分，使用第一组完整评分。",
        "ALL_PROFILE_SCORES_ZERO"=>"所有候选评分均为0，同分时优先保留当前配置，请人工核对图像。",
        "PROFILE_PREDICTION_INVALID"=>"物距曲线预测无效，未激活新配置。",
        "NO_COMPLETED_PROFILE_SCORES"=>"没有完成一组全部候选的有效评分，保留原配置。",
        "INITIALIZATION_BUDGET_TOO_SMALL"=>"当前预算不足以完成一组候选评分及回位，尚未移动电机。",
        "INITIALIZATION_SPEED_OR_MOVING_SCORE_UNAVAILABLE"=>"缺少有效速度表或运动评分能力，请先完成工厂标定。",
        "PROFILE_SPEED_TABLE_INVALID"=>"该物距速度表缺失、失效或与曲线版本不一致，已跳过该配置。",
        "NO_VALID_DISTANCE_PROFILE"=>"当前型号没有同时具备有效双向曲线和速度表的物距配置。",
        "PEAK_EDGE_EXTENSION_LIMIT"=>"最高评分仍在精修边缘，补点达到上限，未可靠包住峰值。",
        "PEAK_AT_MECHANICAL_BOUNDARY"=>"最高评分已到机械行程边界，无法继续包住峰值。",
        "PEAK_EDGE_BUDGET_EXHAUSTED"=>"剩余探测时间不足以补点，已预留回位及合焦预算。",
        "PEAK_NO_VALID_SAMPLES"=>"本倍率没有有效的精修评分。",
        "PEAK_SCORE_CONTRAST_TOO_LOW"=>"精修评分变化不足，未能分辨清晰度峰值。",
        "PEAK_SCORE_NOISE_HIGH"=>"精修最高分处噪声过高，无法可靠确认焦点。",
        "FLAT_UNSTABLE_OR_EDGE_PEAK"=>"评分平坦、不稳定或焦点未被精修范围包围。",
        "SWEEP_SAMPLE_GAP_TOO_LARGE"=>"有效图像采样间隔过大。",
        "PROFILE_SCORE_OR_SCENE_CHANGED"=>"候选清晰度不足或探测期间场景改变。",
        "FINAL_FOCUS_UNSTABLE"=>"返回原倍率后的焦点停稳确认未通过。",
        "PROFILE_MOTOR_DIRECTION_CONFLICT"=>"多个配置的电机方向记录不一致，请重新标定确认。",
        "FINAL_PROFILE_SCORE_TOO_LOW"=>"原倍率的候选清晰度确认未通过。",
        "MODEL_REVISION_CONFLICT"=>"配置版本改变，请刷新后重试。", "LENS_NOT_ACTIVE"=>"请先激活所选镜头型号。",
        "ambiguous"=>"多个配置接近或各倍率判断不一致。", _=>reason
    };
}

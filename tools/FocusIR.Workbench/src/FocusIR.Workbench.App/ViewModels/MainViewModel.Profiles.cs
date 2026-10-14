using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using FocusIR.Workbench.Domain;

namespace FocusIR.Workbench.App.ViewModels;

public sealed record CalibrationLensChoice(string Model, int ProfileCount, bool Active = false)
{
    public string Label => $"{Model} · {ProfileCount} 个物距{(Active ? " · 当前激活" : "")}";
}

public sealed record CalibrationProfileChoice(string Id, string Name, double Distance, bool Active, bool HasModel, string Verification)
{
    public string DisplayName => Name.Trim()=="新物距标定" ? $"{Distance:0.###} m" : Name;
    public string Label => $"{(Name.Trim()=="新物距标定" ? "" : Name+" · ")}{Distance:0.###} m{(Active ? " · 当前激活" : "")}";
}

public sealed partial class MainViewModel
{
    private int _mainSelectedTabIndex;
    private CalibrationLensChoice? _selectedLensModel;
    private CalibrationProfileChoice? _selectedProfile;
    private string _newLensModel = "";
    private string _profileName = "";
    private string _profileDetails = "连接设备后，先选择镜头型号，再选择物距配置。";
    private bool _applyingProfiles;
    private bool _profileLoading;
    private bool _factoryEditorOpen;
    private int _profileLoadGeneration;
    private string? _loadedProfileLens, _loadedProfileId;
    private string? _runningLensModel;
    private string? _runningProfileId;
    private string? _profileTaskOperation, _profileTaskId;

    public int MainSelectedTabIndex
    {
        get => _mainSelectedTabIndex;
        set { _mainSelectedTabIndex = value; OnPropertyChanged(); }
    }

    public ObservableCollection<CalibrationLensChoice> LensModels { get; } = [];
    public ObservableCollection<CalibrationProfileChoice> Profiles { get; } = [];
    public string LensModelCountText => $"设备已保存 {LensModels.Count} 个镜头型号";
    public string ProfileCountText => SelectedLensModel is { } lens
        ? $"{lens.Model}：已保存 {Profiles.Count} 个物距配置"
        : "请先选择或新建镜头型号";
    public string ProfileDetails => _profileDetails;
    public Visibility ProfileControlsVisibility => FactoryControlsVisibility == Visibility.Visible
        && Supports("lens.list") && Supports("profile.list") ? Visibility.Visible : Visibility.Collapsed;
    public bool IsFactoryCalibrationEditorVisible => _factoryEditorOpen && !_profileLoading && SelectedLensModel is not null
        && (SelectedProfile is null || SelectedProfileLoaded);
    public Visibility FactoryCalibrationEditorVisibility => FactoryControlsVisibility == Visibility.Visible
        && IsFactoryCalibrationEditorVisible ? Visibility.Visible : Visibility.Collapsed;

    public string NewLensModel
    {
        get => _newLensModel;
        set { _newLensModel = value; OnPropertyChanged(); RefreshProfileProperties(); }
    }

    public string ProfileName
    {
        get => _profileName;
        set { _profileName = value; OnPropertyChanged(); RefreshOperationProperties(); }
    }

    private bool ProfileNameValid => string.IsNullOrWhiteSpace(ProfileName) ? SelectedProfile is null
        : Encoding.UTF8.GetByteCount(ProfileName.Trim()) < 128;
    private bool NewLensModelValid => ValidLensModelName(NewLensModel.Trim());
    private static bool ValidLensModelName(string name) => !string.IsNullOrWhiteSpace(name)
        && name is not "." and not ".." && Encoding.UTF8.GetByteCount(name) < 128
        && name is not ("active.json" or "device.json" or "dcAfSocket.json" or "dcAfTracking.json"
            or "dcAfFactoryMode" or "dcAfCalibration")
        && !name.Any(c => c is '/' or '\\' || char.IsControl(c));
    private bool SelectedProfileDistanceMatches => SelectedProfile is null
        || double.TryParse(FactoryTargetDistanceMeters, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance)
        && Math.Abs(distance - SelectedProfile.Distance) <= 1e-6;

    public CalibrationLensChoice? SelectedLensModel
    {
        get => _selectedLensModel;
        set
        {
            if (_selectedLensModel == value) return;
            _selectedLensModel = value;
            FactoryLensModel = value?.Model ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProfileCountText));
            RefreshProfileProperties();
            if (_applyingProfiles) return;

            var generation = ++_profileLoadGeneration;
            ResetProfileSelection();
            _factoryEditorOpen = false;
            _profileDetails = value is null ? "请选择或新建镜头型号。" : $"正在读取 {value.Model} 的物距配置…";
            Charts.Begin(null);
            OnPropertyChanged(nameof(ProfileDetails));
            SetProfileLoading(value is not null);
            if (value is not null) _ = LoadSelectedLensAsync(value.Model, generation);
        }
    }

    public CalibrationProfileChoice? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (_selectedProfile == value) return;
            _selectedProfile = value;
            OnPropertyChanged();
            RefreshProfileProperties();
            if (_applyingProfiles) return;

            var generation = ++_profileLoadGeneration;
            _factoryEditorOpen = false;
            Charts.Begin(null);
            if (value is not null && SelectedLensModel is { } lens)
            {
                _profileDetails = $"正在读取 {lens.Model} / {value.Name}…";
                OnPropertyChanged(nameof(ProfileDetails));
                SetProfileLoading(true);
                _ = LoadSelectedProfileAsync(lens.Model, value.Id, generation);
            }
            else
            {
                SetProfileLoading(false);
                RefreshOperationProperties();
            }
        }
    }

    public bool CanBrowseProfiles => !Charts.IsEditing && !_operationBusy && !_factoryTaskFinalizing && _state == ConnectionState.Connected
        && !HasActiveTask() && Supports("lens.list") && Supports("profile.list");
    public bool CanCreateLensModel => CanBrowseProfiles && !_profileLoading && Supports("lens.create")
        && FactoryControlsVisibility == Visibility.Visible && NewLensModelValid;
    public bool CanDeleteLensModel => CanBrowseProfiles && !_profileLoading && SelectedLensModel is not null
        && _lease is null && NoActiveTaskConfirmed() && Supports("lens.delete");
    public bool CanCreateProfile => CanBrowseProfiles && !_profileLoading && SelectedLensModel is not null;
    public bool CanDeleteProfile => CanCreateProfile && SelectedProfileLoaded && SelectedProfile is not null
        && _lease is null && NoActiveTaskConfirmed() && Supports("profile.delete");
    private bool SelectedProfileLoaded => SelectedLensModel?.Model == _loadedProfileLens
        && SelectedProfile is not null && SelectedProfile.Id == _loadedProfileId;
    public bool CanRecalibrateProfile => CanCreateProfile && SelectedProfileLoaded && _lease is null && NoActiveTaskConfirmed()
        && SelectedProfile?.HasModel == true;
    public bool CanRunProfileTask => CanCreateProfile && SelectedProfileLoaded && _lease is null && NoActiveTaskConfirmed()
        && SelectedProfile?.HasModel == true;
    public bool CanVerifyProfile => CanRunProfileTask && FactoryHardwareReady() && Supports("profile.verify");
    public bool CanActivateProfile => CanRunProfileTask && SelectedLensIsActive && Supports("profile.activate");

    private void RefreshProfileProperties()
    {
        Charts.SetEditAvailability(!_operationBusy && !_factoryTaskFinalizing && !_profileLoading
            && _state==ConnectionState.Connected && !HasActiveTask() && NoActiveTaskConfirmed() && _lease is null
            && FactoryControlsVisibility==Visibility.Visible && SelectedLensIsActive && SelectedProfileLoaded && SelectedProfile?.HasModel==true && Supports("profile.update_nodes"),
            _state!=ConnectionState.Connected ? "请连接设备并选择有效物距。"
            : !Supports("profile.update_nodes") ? "当前设备未公布曲线编辑接口，请配套更新设备库。"
            : !SelectedLensIsActive ? "请先激活所选镜头型号，再保存并应用曲线。"
            : !SelectedProfileLoaded || SelectedProfile?.HasModel!=true ? "请先读取一个有有效曲线的物距配置。"
            : HasActiveTask() || !NoActiveTaskConfirmed() ? "请等待设备当前任务结束后再编辑。"
            : _lease is not null ? "请先释放当前控制权，再编辑曲线。"
            : "请等待当前操作或配置读取完成。");
        OnPropertyChanged(nameof(ProfileControlsVisibility));
        OnPropertyChanged(nameof(CanBrowseProfiles));
        OnPropertyChanged(nameof(CanCreateLensModel));
        OnPropertyChanged(nameof(CanDeleteLensModel));
        OnPropertyChanged(nameof(CanCreateProfile));
        OnPropertyChanged(nameof(CanDeleteProfile));
        OnPropertyChanged(nameof(CanRecalibrateProfile));
        OnPropertyChanged(nameof(CanRunProfileTask));
        OnPropertyChanged(nameof(CanVerifyProfile));
        OnPropertyChanged(nameof(CanActivateProfile));
        OnPropertyChanged(nameof(IsFactoryCalibrationEditorVisible));
        OnPropertyChanged(nameof(FactoryCalibrationEditorVisibility));
        RefreshLensProperties();
    }

    private void SetProfileLoading(bool loading)
    {
        _profileLoading = loading;
        if (loading) _loadedProfileLens = _loadedProfileId = null;
        RefreshOperationProperties();
    }

    private bool ProfileRequestCurrent(int generation, string lensModel, string? profileId = null) =>
        generation == _profileLoadGeneration && _state == ConnectionState.Connected
        && SelectedLensModel?.Model == lensModel && (profileId is null || SelectedProfile?.Id == profileId);

    private void ResetProfileSelection()
    {
        var wasApplying = _applyingProfiles;
        _applyingProfiles = true;
        try { Profiles.Clear(); SelectedProfile = null; }
        finally { _applyingProfiles = wasApplying; }
        _loadedProfileLens = _loadedProfileId = null;
        FactoryTargetDistanceMeters = "";
        FactoryTargetIdentifier = "";
        OnPropertyChanged(nameof(ProfileCountText));
    }

    private void ClearProfiles()
    {
        ++_profileLoadGeneration;
        _applyingProfiles = true;
        try
        {
            LensModels.Clear();
            SelectedLensModel = null;
            Profiles.Clear();
            SelectedProfile = null;
        }
        finally { _applyingProfiles = false; }
        _profileLoading = _factoryEditorOpen = _factoryTaskFinalizing = false;
        _loadedProfileLens = _loadedProfileId = null;
        FactoryTargetDistanceMeters = "";
        FactoryTargetIdentifier = "";
        _runningLensModel = _runningProfileId = _curveSavingModelId = null;
        _profileTaskOperation = _profileTaskId = null;
        _activeLensModel = _activeProfileId = ""; _activeLensStatus = "设备尚未激活镜头型号。";
        _lensTaskId = _lensTaskModel = null;
        _lensInitializationStatus = "先激活型号，再将目标置于画面中央，点击镜头初始化。";
        RefreshLensProperties();
        _newLensModel = "";
        _profileName = "";
        _profileDetails = "连接设备后，先选择镜头型号，再选择物距配置。";
        OnPropertyChanged(nameof(NewLensModel));
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(ProfileDetails));
        OnPropertyChanged(nameof(LensModelCountText));
        OnPropertyChanged(nameof(ProfileCountText));
        Charts.Begin(null);
        RefreshOperationProperties();
    }

    private void ApplyLensModelList(JsonElement page, string? preferred = null)
    {
        var model = preferred ?? SelectedLensModel?.Model;
        var active = FactoryDataReader.Text(page, "active_lens_model");
        ApplyActiveLensState(page);
        var wasApplying = _applyingProfiles;
        _applyingProfiles = true;
        try
        {
            LensModels.Clear();
            foreach (var lens in FactoryDataReader.Items(page, "lens_models"))
            {
                var name = FactoryDataReader.Text(lens, "lens_model");
                if (!string.IsNullOrWhiteSpace(name))
                    LensModels.Add(new(name, (int)FactoryDataReader.Number(lens, "profile_count"), name == active));
            }
            SelectedLensModel = LensModels.FirstOrDefault(lens => lens.Model == model)
                ?? LensModels.FirstOrDefault(lens => lens.Model == active) ?? LensModels.FirstOrDefault();
        }
        finally { _applyingProfiles = wasApplying; }
        OnPropertyChanged(nameof(LensModelCountText));
        OnPropertyChanged(nameof(ProfileCountText));
        RefreshOperationProperties();
    }

    private void ApplyProfileList(JsonElement page, string? preferred = null, bool keepNewDraft = false)
    {
        var reportedLens = FactoryDataReader.Text(page, "lens_model");
        if (reportedLens.Length > 0 && reportedLens != SelectedLensModel?.Model) return;
        var id = preferred ?? SelectedProfile?.Id;
        var wasApplying = _applyingProfiles;
        _applyingProfiles = true;
        try
        {
            Profiles.Clear();
            foreach (var profile in FactoryDataReader.Items(page, "profiles"))
            {
                var verification = "未复验";
                if (profile.TryGetProperty("verification", out var report))
                    verification = FactoryDataReader.Boolean(report, "passed") ? "最近复验通过"
                        : FactoryDataReader.Boolean(report, "complete") ? "最近复验未通过" : "最近复验未完成";
                Profiles.Add(new(FactoryDataReader.Text(profile, "profile_id"), FactoryDataReader.Text(profile, "name"),
                    FactoryDataReader.Number(profile, "target_distance_m"), FactoryDataReader.Boolean(profile, "active"),
                    FactoryDataReader.Boolean(profile, "has_model"), verification));
            }
            SelectedProfile = keepNewDraft ? null : Profiles.FirstOrDefault(profile => profile.Id == id)
                ?? Profiles.FirstOrDefault(profile => profile.Active) ?? Profiles.FirstOrDefault();
            _factoryEditorOpen = SelectedLensModel is not null && SelectedProfile?.HasModel != true;
            if (SelectedProfile is { } selected)
            {
                ProfileName = selected.DisplayName;
                _profileDetails = $"{SelectedLensModel?.Model} / {selected.Label} · {selected.Verification}";
            }
            else
            {
                _loadedProfileLens = _loadedProfileId = null;
                if (!keepNewDraft)
                {
                    FactoryTargetDistanceMeters = "";
                    FactoryTargetIdentifier = "";
                    ProfileName = "";
                }
                _profileDetails = SelectedLensModel is null ? "请选择或新建镜头型号。"
                    : keepNewDraft ? $"{SelectedLensModel.Model}：本次新物距未保存，已保留填写的标定参数，可调整后重试。"
                    : $"{SelectedLensModel.Model} 尚无物距配置。填写实测物距和标定参数，成功后保存。";
            }
        }
        finally { _applyingProfiles = wasApplying; }
        OnPropertyChanged(nameof(ProfileCountText));
        OnPropertyChanged(nameof(ProfileDetails));
        RefreshOperationProperties();
    }

    private async Task RefreshProfilesCoreAsync(bool loadSelected, string? preferredLensModel = null)
    {
        var generation = ++_profileLoadGeneration;
        SetProfileLoading(true);
        try
        {
            var lenses = await _connection.ListLensModelsAsync();
            if (generation != _profileLoadGeneration || _state != ConnectionState.Connected) return;
            var previousLensModel = SelectedLensModel?.Model;
            ApplyLensModelList(lenses, preferredLensModel);
            if (SelectedLensModel?.Model != previousLensModel) { ResetProfileSelection(); Charts.Begin(null); }
            if (SelectedLensModel is not { } lens)
            {
                ResetProfileSelection();
                _factoryEditorOpen = false;
                _profileDetails = "暂无镜头型号。请先新建镜头型号，再配置物距标定。";
                OnPropertyChanged(nameof(ProfileDetails));
                Charts.Begin(null);
                return;
            }
            var page = await _connection.ListProfilesAsync(lens.Model);
            if (!ProfileRequestCurrent(generation, lens.Model)) return;
            ApplyProfileList(page);
            if (loadSelected && SelectedProfile is { } selected)
                await LoadProfileCoreAsync(lens.Model, selected.Id, generation);
            else if (SelectedProfile is null) Charts.Begin(null);
        }
        finally
        {
            if (generation == _profileLoadGeneration) SetProfileLoading(false);
        }
    }

    public async Task RefreshProfilesAsync()
    {
        if (!CanBrowseProfiles) return;
        await RunOperationAsync(async () =>
        {
            await RefreshProfilesCoreAsync(true);
            AddActivity($"{LensModelCountText}；{ProfileCountText}。");
        });
    }

    private async Task LoadSelectedLensAsync(string lensModel, int generation)
    {
        try
        {
            var page = await _connection.ListProfilesAsync(lensModel);
            if (!ProfileRequestCurrent(generation, lensModel)) return;
            ApplyProfileList(page);
            if (SelectedProfile is { } selected)
                await LoadProfileCoreAsync(lensModel, selected.Id, generation);
        }
        catch (Exception ex)
        {
            if (ProfileRequestCurrent(generation, lensModel)) ShowProfileReadFailure(ex);
        }
        finally
        {
            if (generation == _profileLoadGeneration) SetProfileLoading(false);
        }
    }

    private async Task LoadSelectedProfileAsync(string lensModel, string id, int generation)
    {
        try { await LoadProfileCoreAsync(lensModel, id, generation); }
        catch (Exception ex)
        {
            if (ProfileRequestCurrent(generation, lensModel, id)) ShowProfileReadFailure(ex);
        }
        finally
        {
            if (generation == _profileLoadGeneration) SetProfileLoading(false);
        }
    }

    private void ShowProfileReadFailure(Exception ex)
    {
        _factoryEditorOpen = false;
        _profileDetails = $"配置读取失败：{ex.Message}；请刷新后重试。";
        _statusNotice = _profileDetails;
        OnPropertyChanged(nameof(ProfileDetails));
        OnPropertyChanged(nameof(StatusNotice));
        AddActivity(_profileDetails);
    }

    private async Task LoadProfileCoreAsync(string lensModel, string id, int generation,
        CancellationToken cancellationToken = default, bool preserveTaskSamples = false)
    {
        var profile = await _connection.GetProfileAsync(lensModel, id, cancellationToken);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!ProfileRequestCurrent(generation, lensModel, id)) return;
            var reportedLens = FactoryDataReader.Text(profile, "lens_model");
            if (reportedLens.Length > 0 && reportedLens != lensModel)
                throw new InvalidOperationException("设备返回的物距配置与所选镜头型号不一致，请刷新后重试。");
            ProfileName = FactoryDataReader.Text(profile, "name", SelectedProfile?.DisplayName ?? "");
            if(ProfileName.Trim()=="新物距标定") ProfileName=$"{FactoryDataReader.Number(profile,"target_distance_m"):0.###} m";
            FactoryTargetDistanceMeters = "";
            FactoryTargetIdentifier = "";
            if (profile.TryGetProperty("recipe", out var recipe)) ApplyProfileRecipe(recipe);
            var hasModel = profile.TryGetProperty("has_model", out _)
                ? FactoryDataReader.Boolean(profile, "has_model") : SelectedProfile?.HasModel == true;
            UpdateLoadedProfileState(lensModel, id, hasModel);
            _factoryEditorOpen = !hasModel;
            _profileDetails = $"{lensModel} / {ProfileName} · {FactoryDataReader.Number(profile, "target_distance_m"):0.###} m"
                + $" · {(FactoryDataReader.Boolean(profile, "active") ? "当前激活" : "已保存，未激活")}"
                + (hasModel ? $" · 模型 {FactoryDataReader.Text(profile, "model_id")}" : " · 尚无有效曲线，需完成标定");
            if (profile.TryGetProperty("verification", out var report))
                _profileDetails += $" · {(FactoryDataReader.Boolean(report, "passed") ? "最近复验通过" : "最近复验未通过或未完成")}"
                    + $"（{FactoryDataReader.Number(report, "checked_positions"):0}/{FactoryDataReader.Number(report, "planned_positions"):0} 位置）";
            OnPropertyChanged(nameof(ProfileDetails));
            if (hasModel) { Charts.ShowProfile(profile, preserveTaskSamples); MainSelectedTabIndex = 1; }
            else if (!preserveTaskSamples) Charts.Begin(null);
            RefreshOperationProperties();
            AddActivity($"已读取镜头 {lensModel} 的物距配置：{ProfileName}。");
        });
    }

    private void UpdateLoadedProfileState(string lensModel, string id, bool hasModel)
    {
        _loadedProfileLens = lensModel;
        _loadedProfileId = id;
        if (SelectedProfile is not { } selected || selected.HasModel == hasModel) return;
        var updated = selected with { HasModel = hasModel };
        var index = Profiles.IndexOf(selected);
        var wasApplying = _applyingProfiles;
        _applyingProfiles = true;
        try
        {
            if (index >= 0) Profiles[index] = updated;
            SelectedProfile = updated;
        }
        finally { _applyingProfiles = wasApplying; }
    }

    private void ApplyProfileRecipe(JsonElement recipe)
    {
        // 镜头型号来自上级选择；设备版本信息始终来自 device.get_info。
        FactoryLensModel = SelectedLensModel?.Model ?? "";
        FactorySampleCount = FactoryDataReader.Text(recipe, "sample_count", "5");
        FactoryMaxNodes = FactoryDataReader.Text(recipe, "max_nodes", "64");
        FactoryInitialNodes = FactoryDataReader.Text(recipe, "initial_nodes_per_direction", FactoryMaxNodes);
        FactorySweepGear = FactoryDataReader.Text(recipe, "focus_sweep_gear", "1");
        FactoryBudgetMilliseconds = FactoryDataReader.Text(recipe, "budget_ms", "900000");
        FactoryClearScoreRatio = FactoryDataReader.Text(recipe, "clear_score_ratio", "0.95");
        FactoryAmbientTemperatureC = FactoryDataReader.Text(recipe, "ambient_temperature_c");
        FactoryUseHybrid = FactoryDataReader.Text(recipe, "sampling_strategy") is "hybrid" or "hybrid_simple";
        FactoryBothDirections = FactoryDataReader.Items(recipe, "directions").Any(value => value.GetString() == "tele_to_wide");
        var distance = FactoryDataReader.Items(recipe, "distance_layers").FirstOrDefault();
        if (distance.ValueKind == JsonValueKind.Object)
        {
            FactoryTargetDistanceMeters = FactoryDataReader.Text(distance, "measured_distance_m");
            FactoryTargetIdentifier = FactoryDataReader.Text(distance, "target_identifier");
        }
    }

    public async Task CreateLensModelAsync()
    {
        if (!CanCreateLensModel) return;
        var name = NewLensModel.Trim();
        await RunOperationAsync(async () =>
        {
            await _connection.CreateLensModelAsync(name);
            NewLensModel = "";
            await RefreshProfilesCoreAsync(true, name);
            AddActivity($"已创建镜头型号 {name}，可填写首个物距的标定参数。");
        });
    }

    public async Task DeleteLensModelAsync()
    {
        if (!CanDeleteLensModel || SelectedLensModel is not { } selected) return;
        var model=selected.Model;
        await RunOperationAsync(async () =>
        {
            _lease=await _connection.AcquireControlAsync("factory");
            try
            {
                await _connection.RefreshSnapshotAndResubscribeAsync();
                if (HasActiveTask() || !NoActiveTaskConfirmed()) throw new InvalidOperationException("设备有活动任务，不能删除镜头型号。");
                var result=await _connection.DeleteLensModelAsync(model);
                ++_profileLoadGeneration;ResetProfileSelection();_factoryEditorOpen=false;Charts.Begin(null);
                await RefreshProfilesCoreAsync(true);
                await _connection.RefreshSnapshotAndResubscribeAsync();
                AddActivity(FactoryDataReader.Boolean(result,"was_active")
                    ? $"已删除镜头型号 {model} 及其全部物距配置；原激活曲线已置为闲置。"
                    : $"已删除镜头型号 {model} 及其全部物距配置。");
            }
            finally
            {
                try { await _connection.ReleaseControlAsync(); } finally { _lease=null;RefreshOperationProperties(); }
            }
        });
    }

    public async Task DeleteProfileAsync()
    {
        if (!CanDeleteProfile || SelectedLensModel is not { } lens || SelectedProfile is not { } selected) return;
        var lensModel=lens.Model;var profileId=selected.Id;var displayName=selected.DisplayName;
        await RunOperationAsync(async () =>
        {
            _lease=await _connection.AcquireControlAsync("factory");
            try
            {
                await _connection.RefreshSnapshotAndResubscribeAsync();
                if (HasActiveTask() || !NoActiveTaskConfirmed()) throw new InvalidOperationException("设备有活动任务，不能删除物距配置。");
                var result=await _connection.DeleteProfileAsync(lensModel,profileId);
                ++_profileLoadGeneration;ResetProfileSelection();_factoryEditorOpen=false;Charts.Begin(null);
                await RefreshProfilesCoreAsync(true,lensModel);
                await _connection.RefreshSnapshotAndResubscribeAsync();
                AddActivity(FactoryDataReader.Boolean(result,"was_active")
                    ? $"已删除 {lensModel} / {displayName}；原激活曲线已置为闲置。"
                    : $"已删除 {lensModel} / {displayName}。");
            }
            finally
            {
                try { await _connection.ReleaseControlAsync(); } finally { _lease=null;RefreshOperationProperties(); }
            }
        });
    }

    public void NewProfile()
    {
        if (!CanCreateProfile) return;
        ++_profileLoadGeneration;
        var wasApplying = _applyingProfiles;
        _applyingProfiles = true;
        try { SelectedProfile = null; }
        finally { _applyingProfiles = wasApplying; }
        _factoryEditorOpen = true;
        _loadedProfileLens = _loadedProfileId = null;
        FactoryTargetDistanceMeters = "";
        FactoryTargetIdentifier = "";
        ProfileName = "";
        _profileDetails = $"{SelectedLensModel!.Model}：新建物距，修改名称、实测物距和目标标识；标定成功后保存。";
        OnPropertyChanged(nameof(ProfileDetails));
        Charts.Begin(null);
        RefreshOperationProperties();
    }

    public void RecalibrateProfile()
    {
        if (!CanRecalibrateProfile || SelectedProfile is not { } selected) return;
        ++_profileLoadGeneration;
        _factoryEditorOpen = true;
        _profileDetails = $"{SelectedLensModel!.Model} / {selected.Label}：可调整标定参数；重新标定成功后更新配置。";
        OnPropertyChanged(nameof(ProfileDetails));
        RefreshOperationProperties();
    }

    private async Task RefreshProfilesAfterTaskAsync(string lensModel, string? profileId,
        CancellationToken cancellationToken, bool preserveTaskSamples = false)
    {
        var generation = -1;bool failedEdit=false;string expectedEditModel="";
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_state != ConnectionState.Connected || SelectedLensModel?.Model != lensModel) return;
            failedEdit=_profileTaskOperation=="edit" && Charts.IsEditing && _factoryResult is { } editResult && ReadString(editResult,"state")!="SUCCEEDED";
            expectedEditModel=Charts.EditingModelId;
            generation = ++_profileLoadGeneration;
            SetProfileLoading(true);
        });
        if (generation < 0) return;
        try
        {
            if(failedEdit && profileId is not null) {
                var actual=await _connection.GetProfileAsync(lensModel,profileId,cancellationToken).ConfigureAwait(false);
                if(FactoryDataReader.Text(actual,"model_id")!=_curveSavingModelId) {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(()=> {
                        if(!ProfileRequestCurrent(generation,lensModel,profileId)) return;
                        var valid=FactoryDataReader.Boolean(actual,"has_model");
                        UpdateLoadedProfileState(lensModel,profileId,valid);_factoryEditorOpen=!valid;
                        if(FactoryDataReader.Text(actual,"model_id")!=expectedEditModel) Charts.RequireCurveReload();
                    });
                    return; // 设备仍是原版本，保留本地草稿和取消恢复基线。
                }
                OnUi(()=> { _statusNotice="任务收尾未成功，但设备曲线版本已提交；正在回读实际保存曲线，请核对。";OnPropertyChanged(nameof(StatusNotice));AddActivity(_statusNotice); });
            }
            var lenses = await _connection.ListLensModelsAsync(cancellationToken).ConfigureAwait(false);
            var page = await _connection.ListProfilesAsync(lensModel, cancellationToken).ConfigureAwait(false);
            string? selectedId = null;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!ProfileRequestCurrent(generation, lensModel)) return;
                var keepNewDraft = SelectedProfile is null && !FactoryDataReader.Items(page, "profiles")
                    .Any(profile => FactoryDataReader.Text(profile, "profile_id") == profileId);
                ApplyLensModelList(lenses, lensModel);
                if (SelectedLensModel?.Model != lensModel) { ResetProfileSelection(); _factoryEditorOpen = false; return; }
                ApplyProfileList(page, profileId, keepNewDraft);
                selectedId = SelectedProfile?.Id;
            });
            if (selectedId is not null)
                await LoadProfileCoreAsync(lensModel, selectedId, generation, cancellationToken, preserveTaskSamples).ConfigureAwait(false);
        }
        finally
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (generation == _profileLoadGeneration) SetProfileLoading(false);
            });
        }
    }

    public async Task StartProfileTaskAsync(bool verify)
    {
        if (!(verify ? CanVerifyProfile : CanActivateProfile) || SelectedProfile is not { } selected
            || SelectedLensModel is not { } lens) return;
        if (!int.TryParse(FactoryBudgetMilliseconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var budget)
            || budget is < 1 or > 1800000)
        {
            _statusNotice = "预算须为 1～1800000 ms。";
            OnPropertyChanged(nameof(StatusNotice));
            return;
        }
        await RunOperationAsync(async () =>
        {
            _lease = await _connection.AcquireControlAsync("factory");
            try
            {
                await _connection.RefreshSnapshotAndResubscribeAsync();
                if (HasActiveTask() || !NoActiveTaskConfirmed() || verify && !FactoryHardwareReady())
                    throw new InvalidOperationException("设备有活动任务或未就绪，请人工确认双轴停稳后重试。");
                var result = await _connection.StartProfileTaskAsync(lens.Model, selected.Id, verify, budget);
                _lastTaskId = ReadString(result, "task_id");
                if (_lastTaskId is null) throw new InvalidOperationException("设备未返回任务编号，操作结果需查询确认。");
                _runningLensModel = lens.Model;
                _runningProfileId = selected.Id;
                _profileTaskOperation = verify ? "verify" : "activate";
                _profileTaskId = _lastTaskId;
                _lastTaskKind = "factory";
                _latestTaskStatus = result;
                _factoryResult = null;
                _factoryEditorOpen = false;
                _statusNotice = verify
                    ? $"设备开始复验 {lens.Model} / {selected.DisplayName}：双向节点和中点分别检查预测位置及附近的 5 个新帧评分。"
                    : $"设备开始激活 {lens.Model} / {selected.DisplayName}，完成后 AF 和随动使用该物距曲线。";
                AddActivity(_statusNotice);
                MainSelectedTabIndex = 1;
                if (verify) { Charts.Begin(_lastTaskId); Charts.SetProfileVerification(); }
                StartTaskMonitor(_lastTaskId, "factory");
            }
            catch
            {
                if (!HasActiveTask()) { await _connection.ReleaseControlAsync(); _lease = null; }
                throw;
            }
        });
    }
}

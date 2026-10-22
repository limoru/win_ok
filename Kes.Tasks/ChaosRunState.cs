namespace Kes.Tasks;

public sealed record EquipmentPendingInstall(
    int Slot,
    string Name,
    string Description,
    string Quality,
    string PreviousName,
    string PreviousDescription,
    string PreviousQuality);

/// <summary>
/// 自动卡厄思模式的单局运行状态，对应 Python 版 <c>ChaosMode.node_status</c>。
/// 每个窗口/会话一个实例，随 <see cref="ChaosModeTask"/> 存活，跨帧保留。
/// </summary>
public sealed class ChaosRunState
{
    public BattleEventFlow BattleEvent { get; } = new();
    public bool Shop { get; set; }
    public bool FlashOrRest { get; set; }
    public bool AwaitingRestConfirmation { get; set; }
    public DateTimeOffset RestConfirmationStartedAt { get; set; }
    public bool ReachFinalBoss { get; set; }
    public bool FinalBossBattle { get; set; }
    public int PassFinalBossCount { get; set; }
    public int TotalRounds { get; set; }
    public int SuccessRounds { get; set; }
    public int NodeCount { get; set; }
    public bool EnterNewNode { get; set; }
    public string NodeType { get; set; } = string.Empty;
    public string PendingEventOptionKey { get; set; } = string.Empty;
    public int PendingEventOptionAttempts { get; set; }
    public bool IsEscaped { get; set; }
    public bool SaveTargetMember { get; set; }
    public int TargetMaskCardPosition { get; set; } = -1;
    public bool GetSpecificFlash { get; set; }
    public int RemovedCardCount { get; set; }
    public int ShopRemovedCardCount { get; set; }
    public int NeutralCardCount { get; set; }
    public bool PendingShopRemoval { get; set; }
    public bool ValidationCardPromptDontShowClicked { get; set; }
    public DateTimeOffset? ValidationCardPromptConfirmedAt { get; set; }
    public int PendingRemovedCardCount { get; set; }
    public string CardSelectionAction { get; set; } = string.Empty;
    public int CardSelectionTargetCount { get; set; }
    public int CardSelectionInputVersion { get; set; }
    public int CardSelectionScrollCount { get; set; }
    public int CardSelectionUnchangedScrollCount { get; set; }
    public string CardSelectionLastFingerprint { get; set; } = string.Empty;
    public SnowSeasonPhase SnowSeasonPhase { get; set; }
    public int SnowSeasonInputVersion { get; set; }
    public DateTimeOffset SnowSeasonLastInputAt { get; set; }
    public string SnowEventFallbackFingerprint { get; set; } = string.Empty;
    public List<int> SnowMemoryRefreshAttempts { get; } = [0, 0, 0];
    public string SnowSelectedMemoryName { get; set; } = string.Empty;
    public int SnowMemberAttemptIndex { get; set; }
    public DateTimeOffset SnowMemberCandidateClickedAt { get; set; }
    public DateTimeOffset SnowMemberConfirmClickedAt { get; set; }
    public int CopyMemberAttemptIndex { get; set; }
    public DateTimeOffset CopyMemberCandidateClickedAt { get; set; }
    public DateTimeOffset CopyMemberConfirmClickedAt { get; set; }
    public int MainMemberFlashAttemptIndex { get; set; }
    public DateTimeOffset MainMemberFlashCandidateClickedAt { get; set; }
    public DateTimeOffset MainMemberFlashConfirmClickedAt { get; set; }
    public bool EquipmentChoiceAwaitingInstall { get; set; }
    public bool EquipmentChoiceConfirmAwaitingAllocation { get; set; }
    public bool EquipmentInstallAwaitingConfirm { get; set; }
    public EquipmentPendingInstall? PendingEquipmentInstall { get; set; }
    public long EquipmentInstallMemberClickedFrameSequence { get; set; }
    public long EquipmentInstallConfirmClickedFrameSequence { get; set; }
    public int EquipmentInstallConfirmAttempts { get; set; }
    public bool BossEquipmentRewardAwaitingAllocation { get; set; }
    public int PauseRequestedBossLayer { get; set; } = -1;
    private readonly Dictionary<string, int> _recognitionMissCounts = new(StringComparer.Ordinal);
    public Dictionary<string, bool> MeditationCards { get; private set; } = new(StringComparer.Ordinal);
    public List<string> EquipmentNames { get; private set; } = ["", "", ""];
    public List<string> EquipmentDescriptions { get; private set; } = ["", "", ""];
    public List<string> EquipmentQualities { get; private set; } = ["", "", ""];

    public int GetRecognitionMissCount(string actionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKey);
        return _recognitionMissCounts.TryGetValue(actionKey, out var count) ? count : 0;
    }

    public int IncrementRecognitionMiss(string actionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKey);
        var count = GetRecognitionMissCount(actionKey) + 1;
        _recognitionMissCounts[actionKey] = count;
        return count;
    }

    public void ResetRecognitionMiss(string actionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKey);
        _recognitionMissCounts.Remove(actionKey);
    }

    public void ResetCardSelectionSearch()
    {
        CardSelectionAction = string.Empty;
        CardSelectionTargetCount = 0;
        CardSelectionInputVersion = 0;
        CardSelectionScrollCount = 0;
        CardSelectionUnchangedScrollCount = 0;
        CardSelectionLastFingerprint = string.Empty;
    }

    public void ResetSnowSeasonState()
    {
        SnowSeasonPhase = SnowSeasonPhase.None;
        SnowSeasonInputVersion = 0;
        SnowSeasonLastInputAt = default;
        SnowEventFallbackFingerprint = string.Empty;
        SnowMemoryRefreshAttempts.Clear();
        SnowMemoryRefreshAttempts.AddRange([0, 0, 0]);
        SnowSelectedMemoryName = string.Empty;
        SnowMemberAttemptIndex = 0;
        SnowMemberCandidateClickedAt = default;
        SnowMemberConfirmClickedAt = default;
    }

    /// <summary>
    /// 重置层状态（对应 Python <c>reset_layer_status</c>）：保留通关计数与任务统计，
    /// 清掉“这一层”的过程标记。过了关底 BOSS 之后调用。
    /// </summary>
    public void ResetLayer()
    {
        var keepPassFinalBoss = PassFinalBossCount;
        var keepTotalRounds = TotalRounds;
        var keepSuccessRounds = SuccessRounds;
        var keepSaveTargetMember = SaveTargetMember;
        var keepMaskPosition = TargetMaskCardPosition;
        var keepSpecificFlash = GetSpecificFlash;
        var keepRemovedCards = RemovedCardCount;
        var keepShopRemovedCards = ShopRemovedCardCount;
        var keepNeutralCards = NeutralCardCount;
        var keepPendingRemovedCardCount = PendingRemovedCardCount;
        var keepMeditationCards = new Dictionary<string, bool>(MeditationCards, StringComparer.Ordinal);
        var keepEquipmentNames = EquipmentNames;
        var keepEquipmentDescriptions = EquipmentDescriptions;
        var keepEquipmentQualities = EquipmentQualities;

        ResetAll();

        PassFinalBossCount = keepPassFinalBoss;
        TotalRounds = keepTotalRounds;
        SuccessRounds = keepSuccessRounds;
        SaveTargetMember = keepSaveTargetMember;
        TargetMaskCardPosition = keepMaskPosition;
        GetSpecificFlash = keepSpecificFlash;
        RemovedCardCount = keepRemovedCards;
        ShopRemovedCardCount = keepShopRemovedCards;
        NeutralCardCount = keepNeutralCards;
        PendingRemovedCardCount = keepPendingRemovedCardCount;
        MeditationCards = keepMeditationCards;
        EquipmentNames = keepEquipmentNames;
        EquipmentDescriptions = keepEquipmentDescriptions;
        EquipmentQualities = keepEquipmentQualities;
    }

    /// <summary>
    /// 重置单局状态（对应 Python <c>reset_mission_status</c>）：保留任务统计与目标成员状态。
    /// </summary>
    public void ResetMission()
    {
        var keepTotalRounds = TotalRounds;
        var keepSuccessRounds = SuccessRounds;
        var keepSaveTargetMember = SaveTargetMember;

        ResetAll();

        TotalRounds = keepTotalRounds;
        SuccessRounds = keepSuccessRounds;
        SaveTargetMember = keepSaveTargetMember;
    }

    private void ResetAll()
    {
        BattleEvent.Complete();
        Shop = false;
        FlashOrRest = false;
        AwaitingRestConfirmation = false;
        RestConfirmationStartedAt = default;
        ReachFinalBoss = false;
        FinalBossBattle = false;
        PassFinalBossCount = 0;
        TotalRounds = 0;
        SuccessRounds = 0;
        NodeCount = 0;
        EnterNewNode = false;
        NodeType = string.Empty;
        PendingEventOptionKey = string.Empty;
        PendingEventOptionAttempts = 0;
        IsEscaped = false;
        SaveTargetMember = false;
        TargetMaskCardPosition = -1;
        GetSpecificFlash = false;
        RemovedCardCount = 0;
        ShopRemovedCardCount = 0;
        NeutralCardCount = 0;
        PendingShopRemoval = false;
        PendingRemovedCardCount = 0;
        ResetCardSelectionSearch();
        ResetSnowSeasonState();
        CopyMemberAttemptIndex = 0;
        CopyMemberCandidateClickedAt = default;
        CopyMemberConfirmClickedAt = default;
        MainMemberFlashAttemptIndex = 0;
        MainMemberFlashCandidateClickedAt = default;
        MainMemberFlashConfirmClickedAt = default;
        EquipmentChoiceAwaitingInstall = false;
        EquipmentChoiceConfirmAwaitingAllocation = false;
        EquipmentInstallAwaitingConfirm = false;
        PendingEquipmentInstall = null;
        EquipmentInstallMemberClickedFrameSequence = 0;
        EquipmentInstallConfirmClickedFrameSequence = 0;
        EquipmentInstallConfirmAttempts = 0;
        BossEquipmentRewardAwaitingAllocation = false;
        PauseRequestedBossLayer = -1;
        _recognitionMissCounts.Clear();
        MeditationCards.Clear();
        EquipmentNames = ["", "", ""];
        EquipmentDescriptions = ["", "", ""];
        EquipmentQualities = ["", "", ""];
    }
}

namespace Kes.Tasks;

/// <summary>
/// 自动卡厄思模式的单局运行状态，对应 Python 版 <c>ChaosMode.node_status</c>。
/// 每个窗口/会话一个实例，随 <see cref="ChaosModeTask"/> 存活，跨帧保留。
/// </summary>
public sealed class ChaosRunState
{
    public bool Shop { get; set; }
    public bool FlashOrRest { get; set; }
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
    public int CopyMemberAttemptIndex { get; set; }
    public int MainMemberFlashAttemptIndex { get; set; }
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
        Shop = false;
        FlashOrRest = false;
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
        CopyMemberAttemptIndex = 0;
        MainMemberFlashAttemptIndex = 0;
        _recognitionMissCounts.Clear();
        MeditationCards.Clear();
        EquipmentNames = ["", "", ""];
        EquipmentDescriptions = ["", "", ""];
        EquipmentQualities = ["", "", ""];
    }
}

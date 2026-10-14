namespace Automation.Core.DeveloperTools;

public static class DecisionSnapshotCatalog
{
    public const string CenterSkipReasonId = "center-skip";
    public const string CenterConfirmReasonId = "center-confirm";
    public const string ValidationCardPromptReasonId = "validation-card-prompt";
    public const string DestinyChoiceReasonId = "destiny-choice";
    public const string BattleSettlementReasonId = "battle-settlement";
    public const string EventOptionsReasonId = "event-options";
    public const string CardRewardReasonId = "card-reward";
    public const string CardAssignReasonId = "card-assign";
    public const string EquipmentChoiceReasonId = "equipment-choice";
    public const string FlashReasonId = "flash";
    public const string ReflashReasonId = "reflash";
    public const string GrantFlashReasonId = "grant-flash";
    public const string ExpeditionResultReasonId = "expedition-result";
    public const string ShopReasonId = "shop";
    public const string RestReasonId = "rest";
    public const string RouteSelectionReasonId = "route-selection";
    public const string FixedClickBlockedReasonId = "fixed-click-blocked";
    public const string FlashEffectNoCardsReasonId = "flash-effect-no-cards";

    private static readonly IReadOnlyDictionary<string, DecisionSnapshotDefinition> Definitions =
        new Dictionary<string, DecisionSnapshotDefinition>(StringComparer.Ordinal)
        {
            [CenterSkipReasonId] = new(
                CenterSkipReasonId,
                "中心跳过/继续提示",
                "这类提示通常占满画面并等待点击中心或提示文字，需要单独标注中心触发区，避免与正常战斗点击混淆。",
                [
                    new("center-trigger", "中心触发区", "点击中心后页面会继续，后续可把这里做成低延迟 ROI。", 0.25, 0.25, 0.75, 0.75),
                    new("center-message", "提示文字区", "用于确认到底是跳过、重试还是继续，避免只靠固定坐标点击。", 0.12, 0.09, 0.88, 0.42)
                ]),
            [CenterConfirmReasonId] = new(
                CenterConfirmReasonId,
                "中心确认按钮",
                "确认按钮位于页面中央，必须和右下角的确认、继续、跳过等通用按钮分开标注，防止处理优先级互相抢占。",
                [
                    new("center-dialog", "中心弹窗区域", "确认按钮的父区域，用于先判断当前是不是中心弹窗。", 0.16, 0.18, 0.84, 0.86),
                    new("center-confirm-button", "中心确认按钮", "按钮区域应单独裁切，后续服务只监听这一小块。", 0.30, 0.52, 0.70, 0.80)
                ]),
            [ValidationCardPromptReasonId] = new(
                ValidationCardPromptReasonId,
                "战利品验证卡弹窗",
                "该弹窗会阻断推进，并且包含复选框与确认按钮，需要明确标注两处区域，避免被通用继续按钮误处理。",
                [
                    new("prompt-title", "弹窗说明文字", "用于识别这是战利品验证卡弹窗。", 0.14, 0.16, 0.86, 0.46),
                    new("prompt-checkbox", "不再显示复选框", "只在首次出现时勾选，需要独立 ROI 记录状态。", 0.24, 0.43, 0.76, 0.63),
                    new("prompt-confirm", "弹窗确认按钮", "确认后应等待页面切换，不能马上再点继续。", 0.22, 0.50, 0.78, 0.80)
                ]),
            [BattleSettlementReasonId] = new(
                BattleSettlementReasonId,
                "战斗结算按钮",
                "结算按钮靠近继续、跳过和离开等按钮，需要裁剪右下角按钮区域，保证只触发结算。",
                [
                    new("settlement-button", "结算按钮", "点击后进入结算流程，需与相邻按钮区分。", 0.84, 0.84, 0.99, 0.99),
                    new("settlement-context", "结算上下文", "保留按钮附近文字，便于 OCR 与模板双重确认。", 0.76, 0.78, 1.00, 1.00)
                ]),
            [EventOptionsReasonId] = new(
                EventOptionsReasonId,
                "事件选项页面",
                "事件选项会按禁止项、必选任务、黑名单和任务优先级分支，需要保存选项图标、描述文字和底部操作区。",
                [
                    new("event-options", "事件选项列表", "用于识别可选事件卡片、图标和描述。", 0.190, 0.825, 0.815, 1.000),
                    new("event-check", "事件确认特征", "check/check2 命中后会优先点击，需要单独保留区域。", 0.390, 0.280, 0.965, 0.725),
                    new("event-task-reward", "任务奖励图标", "任务奖励图标会优先点击，属于独立分支。", 0.420, 0.330, 0.915, 0.630)
                ]),
            [CardRewardReasonId] = new(
                CardRewardReasonId,
                "卡牌奖励页面",
                "卡牌奖励页面需要识别标题、三张候选卡、刷新次数和跳过区域，后续要按优先级选择或刷新。",
                [
                    new("card-reward-title", "卡牌奖励标题", "用于确认当前页面类型。", 0.335, 0.005, 0.650, 0.150),
                    new("card-reward-cards", "奖励候选卡牌", "三张候选卡及其名称、描述和类型都在这里。", 0.070, 0.150, 0.940, 0.760),
                    new("card-reward-refresh", "刷新次数", "命中次数后决定是否刷新候选卡。", 0.100, 0.755, 0.910, 0.860),
                    new("card-reward-skip", "跳过按钮", "没有目标卡时使用，需与相邻按钮区分。", 0.610, 0.875, 1.000, 0.990)
                ]),
            [CardAssignReasonId] = new(
                CardAssignReasonId,
                "卡牌分配页面",
                "卡牌分配会读取卡牌名称、价格、主战员槽位和无法获得状态，需要把卡牌区与主战员区拆开。",
                [
                    new("card-assign-title", "分配标题", "用于确认是接受卡牌还是购买卡牌页面。", 0.640, 0.045, 0.960, 0.205),
                    new("card-assign-card", "待分配卡牌", "用于识别卡牌名称和类型。", 0.180, 0.160, 0.670, 0.760),
                    new("card-assign-members", "主战员槽位", "主战员头像、等级标签和无法获得状态都在这里。", 0.410, 0.160, 0.665, 0.870),
                    new("card-assign-actions", "接受/购买操作", "选择主战员后的接受、购买、取消和跳过按钮。", 0.660, 0.170, 1.000, 0.980)
                ]),
            [EquipmentChoiceReasonId] = new(
                EquipmentChoiceReasonId,
                "装备选择页面",
                "装备页面同时包含候选装备、主战员槽位、价格和提炼按钮，需要按装备名称、类型、品质和槽位分别标注。",
                [
                    new("equipment-title", "装备标题", "用于确认当前是装备选择还是安装界面。", 0.330, 0.045, 0.660, 0.190),
                    new("equipment-hint", "装备页面提示", "提示是否要求选择主战员。", 0.800, 0.075, 1.000, 0.200),
                    new("equipment-candidates", "候选装备", "装备名称、类型、描述和品质图标区域。", 0.160, 0.350, 0.550, 0.700),
                    new("equipment-members", "安装目标主战员", "安装装备时用于选择槽位。", 0.570, 0.270, 0.690, 0.820),
                    new("equipment-actions", "底部操作", "购买、提炼、取消和其他底部按钮。", 0.005, 0.870, 0.998, 1.000)
                ]),
            [FlashReasonId] = new(
                FlashReasonId,
                "闪光分支",
                "闪光确认会改变卡牌强化结果，需要保存闪光按钮、候选卡和确认区域。",
                [
                    new("flash-button", "闪光按钮", "通用闪光操作入口。", 0.820, 0.840, 0.995, 0.990),
                    new("flash-cards", "闪光候选卡牌", "闪光作用对象所在的卡牌区域。", 0.080, 0.130, 0.920, 0.790),
                    new("flash-confirm", "闪光确认区域", "闪光后的确认或继续按钮。", 0.500, 0.850, 1.000, 1.000)
                ]),
            [ReflashReasonId] = new(
                ReflashReasonId,
                "重新闪光分支",
                "重新闪光会替换当前结果，需要保存按钮、当前卡牌以及可能的取消和确认区域。",
                [
                    new("reflash-button", "重新闪光按钮", "重新闪光操作入口。", 0.820, 0.840, 0.995, 0.990),
                    new("reflash-cards", "当前闪光卡牌", "用于比较重新闪光前后的结果。", 0.080, 0.130, 0.920, 0.790),
                    new("reflash-actions", "确认或取消区域", "包含确认、取消、继续等后续操作。", 0.500, 0.850, 1.000, 1.000)
                ]),
            [GrantFlashReasonId] = new(
                GrantFlashReasonId,
                "赋予闪光分支",
                "赋予闪光决定把闪光效果绑定到哪张卡，需要保存按钮、目标卡和确认区域。",
                [
                    new("grant-flash-button", "赋予闪光按钮", "赋予闪光操作入口。", 0.820, 0.840, 0.995, 0.990),
                    new("grant-flash-cards", "目标卡牌", "待赋予闪光的卡牌区域。", 0.080, 0.130, 0.920, 0.790),
                    new("grant-flash-confirm", "赋予确认区域", "赋予后的确认或继续按钮。", 0.500, 0.850, 1.000, 1.000)
                ]),            [DestinyChoiceReasonId] = new(
                DestinyChoiceReasonId,
                "命运选择页面",
                "命运选项决定后续奖励或路线，需要标注提示文字、可选标题区域和确认按钮，避免随机选择时因 OCR 漏项跳错。",
                [
                    new("destiny-prompt", "命运选择提示", "用于确认已经进入命运选择页面。", 0.340, 0.880, 0.660, 0.985),
                    new("destiny-options", "命运标题列表", "可选命运的文字框都在这里，需要保留足够上下文。", 0.195, 0.455, 0.810, 0.625),
                    new("destiny-confirm", "命运确认区域", "如果页面要求二次确认，应单独标注右下角按钮。", 0.760, 0.875, 0.995, 0.990)
                ]),            [ExpeditionResultReasonId] = new(
                ExpeditionResultReasonId,
                "探险结果页面",
                "结束状态会影响本局成功、失败和层数统计，需要标注标题、完成标记和失败标记区域。",
                [
                    new("result-title", "探险结果标题", "用于确认已经进入探险结果页面。", 0.50, 0.05, 0.72, 0.18),
                    new("result-complete", "完成标记", "完成标记用于统计成功。", 0.82, 0.05, 1.00, 0.19),
                    new("result-failed", "失败标记", "失败标记用于统计失败或逃生结果。", 0.19, 0.60, 0.42, 0.81)
                ]),
            [ShopReasonId] = new(
                ShopReasonId,
                "德朗商店页面",
                "商店需要在同一帧里区分商品行、信用点、免费刷新和离开，建议拆成多个稳定 ROI。",
                [
                    new("shop-title", "商店标题", "用于先确认页面类型。", 0.27, 0.05, 0.72, 0.20),
                    new("shop-products", "商品列表", "卡牌、装备、价格和售罄状态都在这一行。", 0.02, 0.65, 0.98, 0.90),
                    new("shop-credit", "信用点", "购买前需要判断当前信用点是否足够。", 0.64, 0.01, 0.86, 0.13),
                    new("shop-refresh", "免费刷新", "没有目标商品时只监听左下角刷新区。", 0.01, 0.88, 0.28, 0.99)
                ]),
            [RestReasonId] = new(
                RestReasonId,
                "休息页面",
                "休息、冥想和德朗商店入口在同一页，区域接近，必须分别标注以免选错操作。",
                [
                    new("rest-action", "休息操作", "用于判断是否可休息并记录点击位置。", 0.15, 0.49, 0.48, 0.87),
                    new("rest-meditate", "冥想操作", "用于读取费用及其可用状态。", 0.65, 0.42, 0.96, 0.82),
                    new("rest-shop-entry", "商店入口", "只在配置允许进入商店时使用。", 0.24, 0.08, 0.47, 0.20)
                ]),
            [RouteSelectionReasonId] = new(
                RouteSelectionReasonId,
                "路线选择页面",
                "节点、特殊图标和小地图规划需要分区域识别，先把当前节点选择画面标注出来。",
                [
                    new("route-nodes", "当前可选节点", "标注主要节点列，供路线优先级和图标识别使用。", 0.64, 0.04, 0.99, 0.92),
                    new("route-minimap", "小地图", "用于规划下一节点和相邻关系。", 0.01, 0.62, 0.39, 0.99),
                    new("route-position", "当前位置", "用于把游戏内当前位置映射到小地图。", 0.32, 0.55, 0.47, 0.77)
                ]),
            [FixedClickBlockedReasonId] = new(
                FixedClickBlockedReasonId,
                "固定位置点击拦截",
                "OCR 未识别到目标时禁止按固定坐标兜底；保存完整画面、OCR 和最近任务日志，用于补充模板或 ROI。",
                [
                    new("blocked-full-frame", "完整画面", "用于复现 OCR 漏识别时的实际页面。", 0.00, 0.00, 1.00, 1.00),
                    new("blocked-action-area", "预期操作区域", "标记原本准备使用固定坐标兜底的附近区域。", 0.54, 0.82, 0.96, 0.99)
                ]),
            [FlashEffectNoCardsReasonId] = new(
                FlashEffectNoCardsReasonId,
                "闪光效果页面未识别到卡牌",
                "页面命中了“请决定闪光效果”，但卡牌特征识别结果为空；保存现场用于修正卡牌图标类型、区域或 OCR 组合。",
                [
                    new("flash-effect-header", "闪光效果提示区", "用于确认“请决定闪光效果”出现的位置。", 0.74, 0.00, 1.00, 0.20),
                    new("flash-effect-cards", "卡牌候选区", "卡牌图标、名称和描述应出现在这一区域。", 0.02, 0.16, 0.99, 0.44),
                    new("flash-effect-actions", "底部操作区", "用于检查跳过、确认或继续按钮是否被 OCR 识别。", 0.50, 0.82, 1.00, 1.00)
                ])
        };

    public static IReadOnlyList<DecisionSnapshotDefinition> All { get; } =
        Definitions.Values.ToArray();

    public static DecisionSnapshotDefinition Resolve(string reasonId)
    {
        if (!string.IsNullOrWhiteSpace(reasonId)
            && Definitions.TryGetValue(reasonId, out var definition))
        {
            return definition;
        }

        var normalized = string.IsNullOrWhiteSpace(reasonId) ? "unknown" : reasonId.Trim();
        return new DecisionSnapshotDefinition(
            normalized,
            normalized,
            "这是未预设说明的判断点；截图已保留，等待人工补充页面类型、操作原因和建议 ROI。",
            []);
    }
}
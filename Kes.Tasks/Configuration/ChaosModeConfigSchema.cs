using Automation.Core.Configuration;

namespace Kes.Tasks.Configuration;

public static class ChaosModeConfigSchema
{
    public const string SeasonSelectorKey = "赛季内容";
    public const string SeasonSelectorGroupName = "赛季内容";
    public const string NoSeasonContent = "不启用赛季内容";
    public const string SongInGalaxySeason = "回荡在银河中的歌声";
    public const string BrokenLightAndClawSeason = "破碎之光与利爪";
    public const string EquipmentGroupName = "装备";
    public const string EngravingGroupName = "回荡在银河中的歌声 · 刻印与面具";
    public const string BrokenLightAndClawGroupName = "破碎之光与利爪 · 欲望卡牌";
    public const string RecognitionFallbackWaitRoundsKey = "识别失败等待轮数";

    public static TaskConfigSchema Instance { get; } = new(
        "ChaosMode",
        "自动卡厄思模式",
        "配置基于 Python 版 ChaosMode 整理；正常流程可运行，赛季专属功能暂缓。",
        [
            new TaskConfigGroupDescriptor(
                SeasonSelectorGroupName,
                [
                    Choice(
                        SeasonSelectorKey,
                        [NoSeasonContent, SongInGalaxySeason, BrokenLightAndClawSeason],
                        NoSeasonContent,
                        "选择后显示对应的赛季专属配置。")
                ],
                IsCollapsible: false),
            new TaskConfigGroupDescriptor(
                "刷取目标",
                [
                    Text("刷存档主战员", "海德玛丽")
                ],
                IsCollapsible: false),
            new TaskConfigGroupDescriptor(
                "常用设置",
                [
                    Choice("游戏语言", ["简体中文", "繁体中文"], "简体中文", "国际服请设置为繁体中文。"),
                    Integer(
                        RecognitionFallbackWaitRoundsKey,
                        2,
                        minimum: 1,
                        maximum: 10,
                        "关键操作连续未识别达到该轮数后，停止固定位置点击并保存诊断现场。"),
                    Integer("存储数据保存上限大于多少", 120, minimum: 0, maximum: 170),
                    Integer("保留大于多少TB的存档", 62000),
                    Boolean("领取奖励(只使用验证卡)", false),
                    Integer("几轮后停止(0为不停止)", 0),
                    Choice("第几层boss前自动暂停", ["不暂停", "1", "2"], "不暂停"),
                    List("必选任务优先级", ["复制"], "命中即直接选择该任务选项，无视拉黑任务过滤；填写规则与“任务优先级”相同。"),
                    List("任务优先级", ["复制", "信用点增加", "移除"], "匹配到包含对应文字的选项时会优先选择，顺序越靠前优先级越高。"),
                    List("拉黑任务", ["咒术卡牌", "压力"], "包含这些关键词的任务选项会被过滤。")
                ],
                IsCollapsible: true,
                IsInitiallyExpanded: true),
            new TaskConfigGroupDescriptor(
                "卡牌策略",
                [
                    List("闪光优先级", [
                        "剑雨感应：生成2张极光剑",
                        "剑雨赋予其回收",
                        "缕光芒80%",
                        "缕光芒216%",
                        "展开极光70%",
                        "展开极光安息唯一",
                        "展开极光200%"
                    ], "卡牌名称和描述无需完整填写，输入几个关键字即可，但顺序须与游戏原文一致。"),
                    List("移除卡牌列表", ["剑幕", "剑光", "水之伞", "海潮的庇护", "作战分析"]),
                    List("闪光卡牌列表", ["展开极光", "剑雨", "缕光芒", "一缕光芒", "万众英雄"]),
                    List("复制卡牌列表", ["展开极光", "剑雨", "缕光芒", "一缕光芒", "万众英雄"]),
                    List("需要冥想的卡牌", ["剑雨", "展开极光"]),
                    Integer("多少信用点以上冥想", 300),
                    Boolean("优先使用金币治疗", true),
                    Boolean("治疗崩溃", true),
                    Boolean("优先移除基础牌", true),
                    Boolean("进入商店", false)
                ],
                IsCollapsible: true,
                IsInitiallyExpanded: false),
            new TaskConfigGroupDescriptor(
                "路线",
                [
                    Boolean("只打第一层", false),
                    List("第一层路线优先级", ["商店", "休息", "事件", "小怪", "精英"], "按顺序选择路线节点；“商店”也是一项可排序节点。"),
                    List("第二层路线优先级", ["商店", "休息", "事件", "小怪", "精英"], "按顺序选择路线节点；“商店”也是一项可排序节点。")
                ],
                IsCollapsible: true,
                IsInitiallyExpanded: false),
            new TaskConfigGroupDescriptor(
                BrokenLightAndClawGroupName,
                [
                    List("欲望卡牌优先级", ["梦之边境"])
                ],
                IsCollapsible: true,
                IsInitiallyExpanded: true),
            new TaskConfigGroupDescriptor(
                EquipmentGroupName,
                [
                    List("装备1号位优先级", ["蚀化臂铠"], "该装备位的目标装备名；留空表示这一位不限装备名。"),
                    List("装备2号位优先级", ["拷问工具箱"], "该装备位的目标装备名；留空表示这一位不限装备名。"),
                    List("装备3号位优先级", ["异象石碑"], "该装备位的目标装备名；留空表示这一位不限装备名。")
                ],
                IsCollapsible: true,
                IsInitiallyExpanded: true),
            new TaskConfigGroupDescriptor(
                EngravingGroupName,
                [
                    Text("指定面具卡牌", "丢弃最多2张卡牌"),
                    Text("面具卡牌刻印", "自身攻击卡牌伤害总量提升30%"),
                    List("装备刻印优先级", []),
                    Integer("刷装备刻印信用点下限", 300, description: "信用点高于该数值才会花钱刷新，否则不刷新；仅影响商店购买。"),
                    Boolean("刻印专业模式", false),
                    Integer("商店删卡信用点下限", 0, description: "仅在“刻印专业模式”下生效。"),
                    Integer("商店删卡次数上限", 2, description: "仅在“刻印专业模式”下生效。")
                ],
                IsCollapsible: true,
                IsInitiallyExpanded: true)
        ]);

    private static TaskConfigOptionDescriptor Boolean(
        string key,
        bool value,
        string description = "")
        => new(key, key, TaskConfigValueKind.Boolean, value, description);

    private static TaskConfigOptionDescriptor Integer(
        string key,
        int value,
        int? minimum = null,
        int? maximum = null,
        string description = "")
        => new(key, key, TaskConfigValueKind.Integer, value, description, null, minimum, maximum);

    private static TaskConfigOptionDescriptor Text(
        string key,
        string value,
        string description = "")
        => new(key, key, TaskConfigValueKind.Text, value, description);

    private static TaskConfigOptionDescriptor Choice(
        string key,
        IReadOnlyList<string> choices,
        string value,
        string description = "")
        => new(key, key, TaskConfigValueKind.Choice, value, description, choices);

    private static TaskConfigOptionDescriptor List(
        string key,
        IReadOnlyList<string> value,
        string description = "")
        => new(key, key, TaskConfigValueKind.StringList, value, description);
}
using Automation.Core.Configuration;

namespace Kes.Tasks.Configuration;

/// <summary>
/// ChaosMode 业务配置读取，集中保存 Python 版的回退和别名规则。
/// </summary>
public sealed class ChaosModeConfig
{
    public const string FirstLayerRoutePriorityKey = "第一层路线优先级";
    public const string SecondLayerRoutePriorityKey = "第二层路线优先级";
    public const string LegacyRoutePriorityKey = "路线优先级";

    private static readonly IReadOnlyDictionary<string, string> RoutePriorityAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["boss"] = "精英",
            ["shop"] = "商店"
        };

    private static readonly IReadOnlyList<string> RoutePriorityDefaultValues =
        ["商店", "休息", "事件", "小怪", "精英"];

    private readonly TaskConfigReader _reader;

    public ChaosModeConfig(TaskConfigReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public static IReadOnlyList<string> RoutePriorityDefault => RoutePriorityDefaultValues;

    public T Get<T>(string key, T fallback)
        => _reader.Get(key, fallback);

    public IReadOnlyList<string> GetCardList(string key)
        => _reader.GetStringList(key);

    public IReadOnlyList<string> GetRoutePriority(int layerIndex = 0)
    {
        var keys = layerIndex <= 0
            ? new[]
            {
                FirstLayerRoutePriorityKey,
                SecondLayerRoutePriorityKey,
                LegacyRoutePriorityKey
            }
            : new[]
            {
                SecondLayerRoutePriorityKey,
                FirstLayerRoutePriorityKey,
                LegacyRoutePriorityKey
            };

        foreach (var key in keys)
        {
            if (_reader.TryGetStringList(key, out var values) && values.Count > 0)
            {
                return NormalizeRoutePriority(values);
            }
        }

        return RoutePriorityDefaultValues.ToArray();
    }

    private static IReadOnlyList<string> NormalizeRoutePriority(IEnumerable<string> values)
    {
        var normalized = values
            .Select(value => RoutePriorityAliases.TryGetValue(value, out var alias) ? alias : value)
            .ToArray();
        return normalized.Length > 0 ? normalized : RoutePriorityDefaultValues.ToArray();
    }
}
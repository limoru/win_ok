using System.Globalization;
using System.Text.Json;

namespace Automation.Core.Configuration;

/// <summary>
/// 统一处理运行配置、schema 默认值和显式兜底值的读取顺序。
/// 对应 Python 的 _get_config_value / _get_card_list 读取语义。
/// </summary>
public sealed class TaskConfigReader
{
    private readonly IReadOnlyDictionary<string, TaskConfigOptionDescriptor> _options;
    private readonly IReadOnlyDictionary<string, object> _values;

    public TaskConfigReader(
        TaskConfigSchema? schema,
        IReadOnlyDictionary<string, object>? values = null)
    {
        _options = schema?.Options.ToDictionary(
            option => option.Key,
            StringComparer.Ordinal) ?? new Dictionary<string, TaskConfigOptionDescriptor>(StringComparer.Ordinal);
        _values = values ?? new Dictionary<string, object>(StringComparer.Ordinal);
    }

    public T Get<T>(string key, T fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_values.TryGetValue(key, out var configured)
            && TryConvert(configured, out T configuredValue))
        {
            return configuredValue;
        }

        if (_options.TryGetValue(key, out var option)
            && TryConvert(option.DefaultValue, out T defaultValue))
        {
            return defaultValue;
        }

        return fallback;
    }

    public IReadOnlyList<string> GetStringList(string key)
        => TryGetStringList(key, out var values) ? values : Array.Empty<string>();

    public bool TryGetStringList(string key, out IReadOnlyList<string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_values.TryGetValue(key, out var configured))
        {
            if (TryConvert(configured, out IReadOnlyList<string> configuredList))
            {
                values = configuredList;
                return true;
            }

            values = Array.Empty<string>();
            return false;
        }

        if (_options.TryGetValue(key, out var option)
            && TryConvert(option.DefaultValue, out IReadOnlyList<string> defaultValue))
        {
            values = defaultValue;
            return true;
        }

        values = Array.Empty<string>();
        return false;
    }

    public bool Contains(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _values.ContainsKey(key) || _options.ContainsKey(key);
    }

    public object? GetRawValue(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_values.TryGetValue(key, out var configured))
        {
            return configured;
        }

        return _options.TryGetValue(key, out var option)
            ? option.DefaultValue
            : null;
    }

    private static bool TryConvert<T>(object? value, out T converted)
    {
        if (value is T typed)
        {
            converted = typed;
            return true;
        }

        if (value is JsonElement element && TryConvertJsonElement(element, out T fromJson))
        {
            converted = fromJson;
            return true;
        }

        try
        {
            if (typeof(T) == typeof(bool) && value is bool boolean)
            {
                converted = (T)(object)boolean;
                return true;
            }

            if (typeof(T) == typeof(string) && value is string text)
            {
                converted = (T)(object)text;
                return true;
            }

            if (typeof(T) == typeof(int) && TryConvertInt32(value, out var integer))
            {
                converted = (T)(object)integer;
                return true;
            }

            if (typeof(T) == typeof(double) && TryConvertDouble(value, out var number))
            {
                converted = (T)(object)number;
                return true;
            }

            if (typeof(T) == typeof(string[]) && value is IEnumerable<string> strings)
            {
                converted = (T)(object)strings.ToArray();
                return true;
            }

            if (typeof(T) == typeof(IReadOnlyList<string>) && value is IEnumerable<string> stringList)
            {
                converted = (T)(object)stringList.ToArray();
                return true;
            }
        }
        catch (Exception exception) when (
            exception is InvalidCastException
            or FormatException
            or OverflowException
            or ArgumentException)
        {
        }

        converted = default!;
        return false;
    }

    private static bool TryConvertJsonElement<T>(JsonElement element, out T converted)
    {
        try
        {
            if (typeof(T) == typeof(bool) && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                converted = (T)(object)element.GetBoolean();
                return true;
            }

            if (typeof(T) == typeof(string) && element.ValueKind == JsonValueKind.String)
            {
                converted = (T)(object)(element.GetString() ?? string.Empty);
                return true;
            }

            if (typeof(T) == typeof(int) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var integer))
            {
                converted = (T)(object)integer;
                return true;
            }

            if (typeof(T) == typeof(double) && element.ValueKind == JsonValueKind.Number)
            {
                converted = (T)(object)element.GetDouble();
                return true;
            }

            if (element.ValueKind == JsonValueKind.Array
                && (typeof(T) == typeof(string[])
                    || typeof(T) == typeof(IReadOnlyList<string>)))
            {
                var values = element.EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty)
                    .ToArray();
                converted = (T)(object)values;
                return true;
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or FormatException
            or OverflowException)
        {
        }

        converted = default!;
        return false;
    }

    private static bool TryConvertInt32(object? value, out int converted)
    {
        switch (value)
        {
            case byte number:
                converted = number;
                return true;
            case sbyte number:
                converted = number;
                return true;
            case short number:
                converted = number;
                return true;
            case ushort number:
                converted = number;
                return true;
            case int number:
                converted = number;
                return true;
            case uint number when number <= int.MaxValue:
                converted = (int)number;
                return true;
            case long number when number is >= int.MinValue and <= int.MaxValue:
                converted = (int)number;
                return true;
            case double number when number >= int.MinValue && number <= int.MaxValue && Math.Truncate(number) == number:
                converted = (int)number;
                return true;
            case string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                converted = parsed;
                return true;
            default:
                converted = 0;
                return false;
        }
    }

    private static bool TryConvertDouble(object? value, out double converted)
    {
        switch (value)
        {
            case float number:
                converted = number;
                return true;
            case double number:
                converted = number;
                return true;
            case decimal number:
                converted = (double)number;
                return true;
            case byte number:
                converted = number;
                return true;
            case short number:
                converted = number;
                return true;
            case int number:
                converted = number;
                return true;
            case long number:
                converted = number;
                return true;
            case string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                converted = parsed;
                return true;
            default:
                converted = 0;
                return false;
        }
    }
}
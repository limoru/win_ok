using System.Text.Json;

namespace Kes.Tasks.Configuration;

public sealed record ChaosEquipmentDefinition(
    int Id,
    string Name,
    int? Slot,
    string Rarity);

/// <summary>
/// Python 装备词典的只读副本，用于根据 OCR 名称确定装备槽位。
/// </summary>
public sealed class ChaosEquipmentCatalog
{
    private readonly IReadOnlyList<ChaosEquipmentDefinition> _definitions;

    private ChaosEquipmentCatalog(IReadOnlyList<ChaosEquipmentDefinition> definitions)
    {
        _definitions = definitions;
    }

    public static ChaosEquipmentCatalog Empty { get; } = new([]);

    public static ChaosEquipmentCatalog Default { get; } = Load(
        Path.Combine(ChaosFeatureAssets.GetDefaultPath(), "equipment_dictionary.json"));

    public IReadOnlyList<ChaosEquipmentDefinition> Definitions => _definitions;

    public static ChaosEquipmentCatalog FromDefinitions(
        IEnumerable<ChaosEquipmentDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return new ChaosEquipmentCatalog(definitions.ToArray());
    }

    public static ChaosEquipmentCatalog Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Empty;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("equipments", out var equipments)
            || equipments.ValueKind != JsonValueKind.Array)
        {
            return Empty;
        }

        var definitions = new List<ChaosEquipmentDefinition>();
        foreach (var item in equipments.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement)
                || !idElement.TryGetInt32(out var id)
                || !item.TryGetProperty("name", out var nameElement))
            {
                continue;
            }

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            int? slot = null;
            if (item.TryGetProperty("slot", out var slotElement)
                && slotElement.TryGetInt32(out var parsedSlot))
            {
                slot = parsedSlot;
            }

            var rarity = item.TryGetProperty("rarity", out var rarityElement)
                ? rarityElement.GetString() ?? string.Empty
                : string.Empty;
            definitions.Add(new ChaosEquipmentDefinition(id, name, slot, rarity));
        }

        return new ChaosEquipmentCatalog(definitions);
    }

    public int? FindSlot(string? ocrName)
    {
        var normalizedOcrName = Normalize(ocrName);
        if (normalizedOcrName.Length == 0)
        {
            return null;
        }

        foreach (var definition in _definitions)
        {
            if (definition.Slot is null)
            {
                continue;
            }

            var normalizedDictionaryName = Normalize(definition.Name);
            if (normalizedDictionaryName.Length > 0
                && (normalizedOcrName.Contains(normalizedDictionaryName, StringComparison.Ordinal)
                    || normalizedDictionaryName.Contains(normalizedOcrName, StringComparison.Ordinal)))
            {
                return definition.Slot;
            }
        }

        return null;
    }

    public bool TryFind(string? ocrName, out ChaosEquipmentDefinition? definition)
    {
        var normalizedOcrName = Normalize(ocrName);
        if (normalizedOcrName.Length == 0)
        {
            definition = null;
            return false;
        }

        foreach (var candidate in _definitions)
        {
            var normalizedDictionaryName = Normalize(candidate.Name);
            if (normalizedDictionaryName.Length > 0
                && (normalizedOcrName.Contains(normalizedDictionaryName, StringComparison.Ordinal)
                    || normalizedDictionaryName.Contains(normalizedOcrName, StringComparison.Ordinal)))
            {
                definition = candidate;
                return true;
            }
        }

        definition = null;
        return false;
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Concat(value.Where(char.IsLetterOrDigit));
    }
}

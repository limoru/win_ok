using System.Collections.ObjectModel;
using Automation.Core.Vision;
using System.Text;
using System.Text.Json;

namespace Kes.Tasks.Configuration;

public sealed class TaskConfigProfileStore
{
    public const int MaxRuntimeTemplatesPerFeature = 5;
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public TaskConfigProfileStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("配置文件路径不能为空。", nameof(filePath));
        }

        _filePath = Path.GetFullPath(filePath);
    }

    public ObservableCollection<TaskConfigProfile> Profiles { get; } = [];
    public string? LastError { get; private set; }

    public static string GetDefaultFilePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KesAutomation",
            "configs",
            "task-profiles.json");

    public void Load()
    {
        Profiles.Clear();
        LastError = null;
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<ProfileDocument>(
                File.ReadAllText(_filePath, Encoding.UTF8),
                _jsonOptions);
            if (document is null)
            {
                return;
            }

            foreach (var item in document.Profiles)
            {
                Profiles.Add(new TaskConfigProfile
                {
                    Id = item.Id,
                    Name = item.Name,
                    ModuleId = item.ModuleId,
                    Values = item.Values.ToDictionary(
                        pair => pair.Key,
                        pair => ConvertJsonValue(pair.Value),
                        StringComparer.Ordinal),
                    RuntimeTemplates = ParseRuntimeTemplates(item.RuntimeTemplates),
                    UpdatedAt = item.UpdatedAt
                });
            }
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
        }
    }

    public TaskConfigProfile Save(
        string moduleId,
        string name,
        IReadOnlyDictionary<string, object> snapshot,
        string? profileId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(snapshot);

        var normalizedName = name.Trim();
        var profile = !string.IsNullOrWhiteSpace(profileId)
            ? Profiles.FirstOrDefault(item => item.Id == profileId)
            : null;
        profile ??= Profiles.FirstOrDefault(item =>
            string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            profile = new TaskConfigProfile
            {
                Name = normalizedName,
                ModuleId = moduleId
            };
            Profiles.Add(profile);
        }

        profile.Name = normalizedName;
        profile.Values = snapshot.ToDictionary(
            pair => pair.Key,
            pair => CloneValue(pair.Value),
            StringComparer.Ordinal);
        profile.UpdatedAt = DateTimeOffset.Now;
        Persist();
        return profile;
    }

    public bool Delete(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var profile = Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            return false;
        }

        var index = Profiles.IndexOf(profile);
        Profiles.RemoveAt(index);
        try
        {
            Persist();
            return true;
        }
        catch
        {
            Profiles.Insert(index, profile);
            throw;
        }
    }

    public void SaveRuntimeTemplate(
        string profileId,
        string key,
        RuntimeFeatureTemplate template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(template);

        var profile = Profiles.FirstOrDefault(item => item.Id == profileId)
            ?? throw new InvalidOperationException($"任务方案不存在：{profileId}");
        if (!profile.RuntimeTemplates.TryGetValue(key, out var templates))
        {
            templates = [];
            profile.RuntimeTemplates[key] = templates;
        }

        var clone = CloneRuntimeTemplate(template);
        if (templates.Any(existing => TemplatesEqual(existing, clone)))
        {
            return;
        }

        templates.Add(clone);
        if (templates.Count > MaxRuntimeTemplatesPerFeature)
        {
            templates.RemoveRange(0, templates.Count - MaxRuntimeTemplatesPerFeature);
        }

        profile.UpdatedAt = DateTimeOffset.Now;
        Persist();
    }

    /// <summary>
    /// Persists a group of feature templates as one avatar variant. If any image in
    /// the group already exists, the whole group is treated as a duplicate so the
    /// per-feature lists cannot drift out of alignment.
    /// </summary>
    public bool SaveRuntimeTemplateBundle(
        string profileId,
        IReadOnlyDictionary<string, RuntimeFeatureTemplate> templates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(templates);
        if (templates.Count == 0)
        {
            return false;
        }

        var profile = Profiles.FirstOrDefault(item => item.Id == profileId)
            ?? throw new InvalidOperationException("Profile not found: " + profileId);
        var updates = new Dictionary<string, List<RuntimeFeatureTemplate>>(StringComparer.Ordinal);
        foreach (var (key, template) in templates)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(template);
            var clone = CloneRuntimeTemplate(template);
            profile.RuntimeTemplates.TryGetValue(key, out var existing);
            if (existing?.Any(item => TemplatesEqual(item, clone)) == true)
            {
                return false;
            }

            var updated = existing is null
                ? []
                : existing.Select(CloneRuntimeTemplate).ToList();
            updated.Add(clone);
            if (updated.Count > MaxRuntimeTemplatesPerFeature)
            {
                updated.RemoveRange(0, updated.Count - MaxRuntimeTemplatesPerFeature);
            }

            updates[key] = updated;
        }

        foreach (var (key, templatesForFeature) in updates)
        {
            profile.RuntimeTemplates[key] = templatesForFeature;
        }

        profile.UpdatedAt = DateTimeOffset.Now;
        Persist();
        return true;
    }

    public bool RemoveRuntimeTemplateAt(
        string profileId,
        IEnumerable<string> keys,
        int index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var profile = Profiles.FirstOrDefault(item => item.Id == profileId)
            ?? throw new InvalidOperationException("Profile not found: " + profileId);
        var changed = false;
        foreach (var key in keys
                     .Where(key => !string.IsNullOrWhiteSpace(key))
                     .Distinct(StringComparer.Ordinal))
        {
            if (!profile.RuntimeTemplates.TryGetValue(key, out var templates)
                || index >= templates.Count)
            {
                continue;
            }

            templates.RemoveAt(index);
            changed = true;
            if (templates.Count == 0)
            {
                profile.RuntimeTemplates.Remove(key);
            }
        }

        if (!changed)
        {
            return false;
        }

        profile.UpdatedAt = DateTimeOffset.Now;
        Persist();
        return true;
    }

    public void ClearRuntimeTemplates(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var profile = Profiles.FirstOrDefault(item => item.Id == profileId)
            ?? throw new InvalidOperationException($"任务方案不存在：{profileId}");
        if (profile.RuntimeTemplates.Count == 0)
        {
            return;
        }

        profile.RuntimeTemplates.Clear();
        profile.UpdatedAt = DateTimeOffset.Now;
        Persist();
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = new ProfileDocument
        {
            Profiles = Profiles.Select(profile => new ProfileDocumentItem
            {
                Id = profile.Id,
                Name = profile.Name,
                ModuleId = profile.ModuleId,
                Values = profile.Values.ToDictionary(
                    pair => pair.Key,
                    pair => JsonSerializer.SerializeToElement(pair.Value, _jsonOptions),
                    StringComparer.Ordinal),
                RuntimeTemplates = profile.RuntimeTemplates.ToDictionary(
                    pair => pair.Key,
                    pair => JsonSerializer.SerializeToElement(
                        pair.Value.Select(CloneRuntimeTemplate).ToArray(),
                        _jsonOptions),
                    StringComparer.Ordinal),
                UpdatedAt = profile.UpdatedAt
            }).ToList()
        };

        var json = JsonSerializer.Serialize(document, _jsonOptions);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, _filePath, true);
        LastError = null;
    }

    private static RuntimeFeatureTemplate CloneRuntimeTemplate(RuntimeFeatureTemplate template)
        => template with { Pixels = template.Pixels.ToArray() };

    private Dictionary<string, List<RuntimeFeatureTemplate>> ParseRuntimeTemplates(
        IReadOnlyDictionary<string, JsonElement>? runtimeTemplates)
    {
        var result = new Dictionary<string, List<RuntimeFeatureTemplate>>(StringComparer.Ordinal);
        if (runtimeTemplates is null)
        {
            return result;
        }

        foreach (var (key, element) in runtimeTemplates)
        {
            List<RuntimeFeatureTemplate>? templates = element.ValueKind switch
            {
                JsonValueKind.Array => JsonSerializer.Deserialize<List<RuntimeFeatureTemplate>>(
                    element.GetRawText(),
                    _jsonOptions),
                JsonValueKind.Object => JsonSerializer.Deserialize<RuntimeFeatureTemplate>(
                    element.GetRawText(),
                    _jsonOptions) is { } template
                    ? [template]
                    : null,
                _ => null
            };
            if (templates is null || templates.Count == 0)
            {
                continue;
            }

            result[key] = templates.Select(CloneRuntimeTemplate).ToList();
        }

        return result;
    }

    private static bool TemplatesEqual(
        RuntimeFeatureTemplate first,
        RuntimeFeatureTemplate second)
        => first.Width == second.Width
           && first.Height == second.Height
           && first.Pixels.AsSpan().SequenceEqual(second.Pixels);

    private static object CloneValue(object value)
        => value switch
        {
            string[] values => values.ToArray(),
            IReadOnlyList<string> values => values.ToArray(),
            _ => value
        };

    private static object ConvertJsonValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt32(out var integer) => integer,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Array => element.EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .ToArray(),
            JsonValueKind.Null => string.Empty,
            _ => element.GetRawText()
        };

    private sealed class ProfileDocument
    {
        public int SchemaVersion { get; init; } = 1;
        public List<ProfileDocumentItem> Profiles { get; init; } = [];
    }

    private sealed class ProfileDocumentItem
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string ModuleId { get; init; } = string.Empty;
        public Dictionary<string, JsonElement> Values { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonElement> RuntimeTemplates { get; init; } =
            new(StringComparer.Ordinal);
        public DateTimeOffset UpdatedAt { get; init; }
    }
}

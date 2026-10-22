using Automation.Core.Vision;

namespace Kes.Tasks.Configuration;

public sealed class TaskConfigProfileRuntimeTemplateStore : IRuntimeFeatureTemplateStore
{
    private const char ScopeSeparator = '\u001F';
    private readonly TaskConfigProfileStore _store;
    private readonly TaskConfigProfile _profile;
    private readonly string _scope;

    public TaskConfigProfileRuntimeTemplateStore(
        TaskConfigProfileStore store,
        TaskConfigProfile profile,
        string scope)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        _scope = scope.Trim();
    }

    public IReadOnlyList<RuntimeFeatureTemplate> Load(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        if (!_profile.RuntimeTemplates.TryGetValue(CreateKey(featureName), out var stored))
        {
            return [];
        }

        return stored
            .Select(template => Clone(template) with { FeatureName = featureName })
            .ToArray();
    }

    public void Save(RuntimeFeatureTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(template.FeatureName);
        _store.SaveRuntimeTemplate(
            _profile.Id,
            CreateKey(template.FeatureName),
            Clone(template));
    }

    public void SaveBundle(IReadOnlyDictionary<string, RuntimeFeatureTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        var scoped = templates.ToDictionary(
            pair => CreateKey(pair.Key),
            pair => Clone(pair.Value),
            StringComparer.Ordinal);
        _store.SaveRuntimeTemplateBundle(_profile.Id, scoped);
    }

    public static string CreateScope(string profileId, string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        return string.Concat(profileId, "|", targetName.Trim());
    }

    public static string CreateKey(string scope, string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        return string.Concat(scope, ScopeSeparator, featureName);
    }

    private string CreateKey(string featureName) => CreateKey(_scope, featureName);

    private static RuntimeFeatureTemplate Clone(RuntimeFeatureTemplate template)
        => template with { Pixels = template.Pixels.ToArray() };
}

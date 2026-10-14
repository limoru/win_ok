using Automation.Core.Vision;
using Kes.Tasks.Configuration;
using Xunit;

namespace Automation.Core.Tests;

public sealed class TaskConfigProfileStoreTests
{
    [Fact]
    public void SaveAndReloadPreservesSnapshotValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profiles.json");

        try
        {
            var store = new TaskConfigProfileStore(path);
            store.Save(
                "ChaosMode",
                "方案 A",
                new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["threshold"] = 42,
                    ["language"] = "简体中文",
                    ["rewardCards"] = new[] { "梦之边境", "破碎之光" }
                });

            var reloaded = new TaskConfigProfileStore(path);
            reloaded.Load();

            Assert.Null(reloaded.LastError);
            var profile = Assert.Single(reloaded.Profiles);
            Assert.Equal("方案 A", profile.Name);
            Assert.Equal("ChaosMode", profile.ModuleId);
            Assert.Equal(true, profile.Values["enabled"]);
            Assert.Equal(42, profile.Values["threshold"]);
            Assert.Equal("简体中文", profile.Values["language"]);
            Assert.Equal(
                ["梦之边境", "破碎之光"],
                Assert.IsType<string[]>(profile.Values["rewardCards"]));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void SaveAndReloadPreservesRuntimeFeatureTemplates()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profiles.json");

        try
        {
            var store = new TaskConfigProfileStore(path);
            var profile = store.Save(
                "ChaosMode",
                "方案 A",
                new Dictionary<string, object> { ["target"] = "海德玛丽" });
            var first = new RuntimeFeatureTemplate(
                "target_member",
                2,
                2,
                Enumerable.Range(0, 16).Select(value => (byte)value).ToArray());
            var second = new RuntimeFeatureTemplate(
                "target_member",
                2,
                2,
                Enumerable.Range(16, 16).Select(value => (byte)value).ToArray());
            store.SaveRuntimeTemplate(profile.Id, "scope|target_member", first);
            store.SaveRuntimeTemplate(profile.Id, "scope|target_member", second);

            var reloaded = new TaskConfigProfileStore(path);
            reloaded.Load();

            Assert.Null(reloaded.LastError);
            var loadedProfile = Assert.Single(reloaded.Profiles);
            var loadedTemplates = Assert.Single(loadedProfile.RuntimeTemplates).Value;
            Assert.Equal(2, loadedTemplates.Count);
            Assert.Equal("target_member", loadedTemplates[0].FeatureName);
            Assert.Equal(first.Pixels, loadedTemplates[0].Pixels);
            Assert.Equal(second.Pixels, loadedTemplates[1].Pixels);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void SaveWithExistingNameUpdatesProfile()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profiles.json");

        try
        {
            var store = new TaskConfigProfileStore(path);
            var first = store.Save(
                "ChaosMode",
                "刷图方案",
                new Dictionary<string, object> { ["threshold"] = 10 });
            var second = store.Save(
                "ChaosMode",
                "刷图方案",
                new Dictionary<string, object> { ["threshold"] = 20 });

            Assert.Same(first, second);
            Assert.Single(store.Profiles);
            Assert.Equal(20, store.Profiles[0].Values["threshold"]);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void DeleteRemovesProfileFromDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profiles.json");

        try
        {
            var store = new TaskConfigProfileStore(path);
            var profile = store.Save(
                "ChaosMode",
                "方案 A",
                new Dictionary<string, object> { ["enabled"] = true });

            Assert.True(store.Delete(profile.Id));
            Assert.Empty(store.Profiles);

            var reloaded = new TaskConfigProfileStore(path);
            reloaded.Load();
            Assert.Empty(reloaded.Profiles);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
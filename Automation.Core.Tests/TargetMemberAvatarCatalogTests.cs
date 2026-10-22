using Automation.Core.Frames;
using Kes.Tasks.Configuration;
using Xunit;

namespace Automation.Core.Tests;

public sealed class TargetMemberAvatarCatalogTests
{
    [Fact]
    public void MultipleAvatarVariantsRemainAlignedAcrossAllFeatures()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profiles.json");
        const string targetName = "海德玛丽";

        try
        {
            var store = new TaskConfigProfileStore(path);
            var profile = store.Save(
                "ChaosMode",
                "头像测试",
                new Dictionary<string, object>
                {
                    ["刷存档主战员"] = targetName
                });

            Assert.True(TargetMemberAvatarCatalog.AddAvatar(
                store,
                profile.Id,
                targetName,
                CreateSolidFrame(4, 4, 220, 30, 40, 255)));
            Assert.True(TargetMemberAvatarCatalog.AddAvatar(
                store,
                profile.Id,
                targetName,
                CreateSolidFrame(4, 4, 30, 90, 220, 255)));

            Assert.Equal(2, TargetMemberAvatarCatalog.Load(profile, targetName).Count);
            var runtimeStore = new TaskConfigProfileRuntimeTemplateStore(
                store,
                profile,
                TaskConfigProfileRuntimeTemplateStore.CreateScope(profile.Id, targetName));
            foreach (var feature in TargetMemberAvatarCatalog.Features)
            {
                Assert.Equal(2, runtimeStore.Load(feature.Name).Count);
            }

            Assert.True(TargetMemberAvatarCatalog.RemoveAvatarAt(
                store,
                profile.Id,
                targetName,
                0));
            Assert.Single(TargetMemberAvatarCatalog.Load(profile, targetName));
            foreach (var feature in TargetMemberAvatarCatalog.Features)
            {
                Assert.Single(runtimeStore.Load(feature.Name));
            }

            var reloaded = new TaskConfigProfileStore(path);
            reloaded.Load();
            var reloadedProfile = Assert.Single(reloaded.Profiles);
            Assert.Single(TargetMemberAvatarCatalog.Load(reloadedProfile, targetName));
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
    public void DuplicateAvatarDoesNotPartiallyAppendFeatureBundle()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profiles.json");
        const string targetName = "海德玛丽";

        try
        {
            var store = new TaskConfigProfileStore(path);
            var profile = store.Save(
                "ChaosMode",
                "头像测试",
                new Dictionary<string, object>
                {
                    ["刷存档主战员"] = targetName
                });
            var avatar = CreateSolidFrame(4, 4, 20, 40, 60, 255);

            Assert.True(TargetMemberAvatarCatalog.AddAvatar(store, profile.Id, targetName, avatar));
            Assert.False(TargetMemberAvatarCatalog.AddAvatar(store, profile.Id, targetName, avatar));
            Assert.Single(TargetMemberAvatarCatalog.Load(profile, targetName));
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
    public void RenamingProfileWithIdKeepsAvatarBundle()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profiles.json");
        const string targetName = "海德玛丽";

        try
        {
            var store = new TaskConfigProfileStore(path);
            var profile = store.Save(
                "ChaosMode",
                "旧方案名",
                new Dictionary<string, object>
                {
                    ["刷存档主战员"] = targetName
                });
            Assert.True(TargetMemberAvatarCatalog.AddAvatar(
                store,
                profile.Id,
                targetName,
                CreateSolidFrame(4, 4, 10, 20, 30, 255)));

            var renamed = store.Save(
                "ChaosMode",
                "新方案名",
                profile.Values,
                profile.Id);

            Assert.Same(profile, renamed);
            Assert.Equal("新方案名", renamed.Name);
            Assert.Single(TargetMemberAvatarCatalog.Load(renamed, targetName));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static BgraFrame CreateSolidFrame(
        int width,
        int height,
        byte blue,
        byte green,
        byte red,
        byte alpha)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = blue;
            pixels[index + 1] = green;
            pixels[index + 2] = red;
            pixels[index + 3] = alpha;
        }

        return new BgraFrame(width, height, pixels);
    }
}

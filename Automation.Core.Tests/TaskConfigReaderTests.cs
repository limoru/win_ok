using System.Text.Json;
using Automation.Core.Configuration;
using Xunit;

namespace Automation.Core.Tests;

public sealed class TaskConfigReaderTests
{
    private static readonly TaskConfigSchema Schema = new(
        "test",
        "Test",
        string.Empty,
        [
            new TaskConfigGroupDescriptor(
                "General",
                [
                    new TaskConfigOptionDescriptor("count", "Count", TaskConfigValueKind.Integer, 3),
                    new TaskConfigOptionDescriptor("enabled", "Enabled", TaskConfigValueKind.Boolean, true),
                    new TaskConfigOptionDescriptor("name", "Name", TaskConfigValueKind.Text, "default"),
                    new TaskConfigOptionDescriptor(
                        "items",
                        "Items",
                        TaskConfigValueKind.StringList,
                        new[] { "a", "b" })
                ])
        ]);

    [Fact]
    public void GetUsesRuntimeValueBeforeSchemaDefault()
    {
        var reader = new TaskConfigReader(
            Schema,
            new Dictionary<string, object> { ["count"] = 9 });

        Assert.Equal(9, reader.Get("count", 1));
    }

    [Fact]
    public void GetFallsBackToSchemaDefaultBeforeExplicitFallback()
    {
        var reader = new TaskConfigReader(Schema);

        Assert.Equal(3, reader.Get("count", 1));
        Assert.True(reader.Get("enabled", false));
        Assert.Equal("default", reader.Get("name", "fallback"));
    }

    [Fact]
    public void GetReturnsExplicitFallbackForUnknownKey()
    {
        var reader = new TaskConfigReader(Schema);

        Assert.Equal(42, reader.Get("missing", 42));
    }

    [Fact]
    public void GetStringListReturnsRuntimeArray()
    {
        var reader = new TaskConfigReader(
            Schema,
            new Dictionary<string, object> { ["items"] = new[] { "x", "y" } });

        Assert.Equal(["x", "y"], reader.GetStringList("items"));
    }

    [Fact]
    public void GetStringListReturnsEmptyForNonListValue()
    {
        var reader = new TaskConfigReader(
            Schema,
            new Dictionary<string, object> { ["items"] = "not-a-list" });

        Assert.Empty(reader.GetStringList("items"));
    }

    [Fact]
    public void GetConvertsJsonNumber()
    {
        var reader = new TaskConfigReader(
            Schema,
            new Dictionary<string, object> { ["count"] = JsonSerializer.SerializeToElement(12) });

        Assert.Equal(12, reader.Get("count", 0));
    }
}
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Automation.MemoryProbe;

if (args.Length == 0)
{
    PrintUsage();
    return 64;
}

var selector = args[0];
var scanTextIndex = Array.FindIndex(args, value =>
    string.Equals(value, "--scan-text", StringComparison.OrdinalIgnoreCase));
var scanText = scanTextIndex >= 0 && scanTextIndex + 1 < args.Length
    ? args[scanTextIndex + 1]
    : null;
var maxMegabytes = ReadLongOption(args, "--max-mb", 256);
var maxMatches = (int)ReadLongOption(args, "--max-matches", 20);

var processId = ResolveProcessId(selector, out var resolutionError);
if (processId is null)
{
    Console.Error.WriteLine(resolutionError);
    return 3;
}

using var session = ProcessMemorySession.TryOpen(processId.Value, out var probeResult);
var serializerOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() }
};
Console.WriteLine(JsonSerializer.Serialize(probeResult, serializerOptions));

if (session is null)
{
    return 2;
}

if (string.IsNullOrEmpty(scanText))
{
    return 0;
}

var scanResult = ProcessMemoryTextScanner.Scan(
    session,
    new TextScanOptions(
        scanText,
        checked(maxMegabytes * 1024L * 1024L),
        maxMatches));
Console.WriteLine(JsonSerializer.Serialize(scanResult, serializerOptions));
return scanResult.Error is null ? 0 : 2;

static int? ResolveProcessId(string selector, out string error)
{
    error = string.Empty;
    if (int.TryParse(selector, out var processId))
    {
        return processId;
    }

    var processName = Path.GetFileNameWithoutExtension(selector);
    var matches = Process.GetProcessesByName(processName)
        .OrderBy(process => process.Id)
        .ToArray();
    if (matches.Length == 0)
    {
        error = $"Process not found: {selector}";
        return null;
    }

    if (matches.Length > 1)
    {
        error = $"Multiple processes matched {selector}: {string.Join(", ", matches.Select(process => process.Id))}. Pass a PID explicitly.";
        return null;
    }

    return matches[0].Id;
}

static long ReadLongOption(string[] arguments, string name, long defaultValue)
{
    var index = Array.FindIndex(arguments, value =>
        string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    if (index < 0)
    {
        return defaultValue;
    }

    if (index + 1 >= arguments.Length
        || !long.TryParse(arguments[index + 1], out var value)
        || value <= 0)
    {
        throw new ArgumentException($"Option {name} requires a positive integer.");
    }

    return value;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  Automation.MemoryProbe <pid|process-name>");
    Console.Error.WriteLine("  Automation.MemoryProbe <pid|process-name> --scan-text <text> [--max-mb 256] [--max-matches 20]");
}
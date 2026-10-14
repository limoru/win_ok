namespace Kes.Tasks;

public static class ChaosFeatureAssets
{
    public static string GetDefaultPath()
        => Path.Combine(AppContext.BaseDirectory, "Assets", "ChaosFeatures");
}
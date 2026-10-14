namespace Automation.Vision.Onnx;

public sealed record PpOcrModelPaths(
    string ModelDirectory,
    string DetectionModelPath,
    string RecognitionModelPath,
    string CharacterDictionaryPath);

public static class PpOcrModelLocator
{
    private const string ModelDirectoryName = "ppocrv5";
    private const string DetectionModelFileName = "det.onnx";
    private const string RecognitionModelFileName = "rec.onnx";
    private const string DictionaryFileName = "ppocrv5_dict.txt";

    public static bool TryFind(out PpOcrModelPaths paths, out string status)
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("KES_PPOCRV5_MODEL_DIR")
        };

        candidates.AddRange(EnumerateParentCandidates(AppContext.BaseDirectory));
        candidates.AddRange(EnumerateParentCandidates(Environment.CurrentDirectory));
        candidates.Add(@"D:\work\ok\ok-kes\.venv\Lib\site-packages\onnxocr\models\ppocrv5");

        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var directory = Path.GetFullPath(candidate!);
            var detectionModel = Path.Combine(directory, "det", DetectionModelFileName);
            if (!File.Exists(detectionModel))
            {
                detectionModel = Path.Combine(directory, DetectionModelFileName);
            }

            var recognitionModel = Path.Combine(directory, "rec", RecognitionModelFileName);
            if (!File.Exists(recognitionModel))
            {
                recognitionModel = Path.Combine(directory, RecognitionModelFileName);
            }

            var dictionary = Path.Combine(directory, DictionaryFileName);
            if (File.Exists(detectionModel)
                && File.Exists(recognitionModel)
                && File.Exists(dictionary))
            {
                paths = new PpOcrModelPaths(
                    directory,
                    detectionModel,
                    recognitionModel,
                    dictionary);
                status = $"已找到 PP-OCRv5 检测与识别模型：{directory}";
                return true;
            }
        }

        paths = new PpOcrModelPaths(string.Empty, string.Empty, string.Empty, string.Empty);
        status = "未找到完整的 PP-OCRv5 检测与识别模型；可设置 KES_PPOCRV5_MODEL_DIR 指向模型目录。";
        return false;
    }

    private static IEnumerable<string> EnumerateParentCandidates(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            yield return Path.Combine(directory.FullName, "models", ModelDirectoryName);
            yield return Path.Combine(
                directory.FullName,
                ".venv",
                "Lib",
                "site-packages",
                "onnxocr",
                "models",
                ModelDirectoryName);
        }
    }
}
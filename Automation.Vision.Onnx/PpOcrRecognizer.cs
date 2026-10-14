using System.Runtime.InteropServices;
using System.Text;
using Automation.Core.Frames;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace Automation.Vision.Onnx;

public sealed record OcrRecognitionResult(string Text, double Score);

public sealed class PpOcrRecognizer : IDisposable
{
    private const int TargetHeight = 48;
    private const int MinimumTargetWidth = 320;
    private const int MaximumTargetWidth = 1920;

    private readonly object _gate = new();
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string[] _characters;

    private PpOcrRecognizer(
        InferenceSession session,
        string inputName,
        string[] characters,
        string modelDirectory)
    {
        _session = session;
        _inputName = inputName;
        _characters = characters;
        ModelDirectory = modelDirectory;
    }

    public string ModelDirectory { get; }

    public static bool TryCreateDefault(out PpOcrRecognizer? recognizer, out string status)
    {
        if (!PpOcrModelLocator.TryFind(out var paths, out status))
        {
            recognizer = null;
            return false;
        }

        return TryCreate(paths, out recognizer, out status);
    }

    public static bool TryCreate(
        PpOcrModelPaths paths,
        out PpOcrRecognizer? recognizer,
        out string status)
    {
        try
        {
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                InterOpNumThreads = 1
            };

            var session = new InferenceSession(paths.RecognitionModelPath, options);
            var inputName = session.InputMetadata.Keys.First();
            var dictionary = File.ReadAllLines(paths.CharacterDictionaryPath, Encoding.UTF8);
            var characters = new string[dictionary.Length + 2];
            characters[0] = "blank";
            Array.Copy(dictionary, 0, characters, 1, dictionary.Length);
            characters[^1] = " ";

            recognizer = new PpOcrRecognizer(session, inputName, characters, paths.ModelDirectory);
            status = $"PP-OCRv5 识别模型已加载：{paths.RecognitionModelPath}";
            return true;
        }
        catch (Exception exception)
        {
            recognizer = null;
            status = $"PP-OCRv5 识别模型加载失败：{exception.Message}";
            return false;
        }
    }

    public OcrRecognitionResult Recognize(BgraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            throw new ArgumentException("The frame is empty.", nameof(frame));
        }

        var input = CreateInputTensor(frame);
        lock (_gate)
        {
            using var results = _session.Run(
                new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
            var output = results[0].AsTensor<float>();
            return Decode(output);
        }
    }

    private static DenseTensor<float> CreateInputTensor(BgraFrame frame)
    {
        using var bgra = new Mat(frame.Height, frame.Width, MatType.CV_8UC4);
        Marshal.Copy(frame.Pixels, 0, bgra.Data, frame.Pixels.Length);

        using var bgr = bgra.CvtColor(ColorConversionCodes.BGRA2BGR);
        var ratio = frame.Width / (double)frame.Height;
        var targetWidth = Math.Clamp(
            (int)Math.Ceiling(TargetHeight * ratio),
            MinimumTargetWidth,
            MaximumTargetWidth);
        var resizedWidth = Math.Clamp(
            (int)Math.Ceiling(TargetHeight * ratio),
            1,
            targetWidth);
        using var resized = bgr.Resize(
            new Size(resizedWidth, TargetHeight),
            0,
            0,
            InterpolationFlags.Linear);
        using var continuous = resized.Clone();

        var resizedBytes = new byte[TargetHeight * resizedWidth * 3];
        Marshal.Copy(continuous.Data, resizedBytes, 0, resizedBytes.Length);

        var tensor = new DenseTensor<float>(new[] { 1, 3, TargetHeight, targetWidth });
        for (var y = 0; y < TargetHeight; y++)
        {
            for (var x = 0; x < resizedWidth; x++)
            {
                var sourceIndex = ((y * resizedWidth) + x) * 3;
                for (var channel = 0; channel < 3; channel++)
                {
                    tensor[0, channel, y, x] = (resizedBytes[sourceIndex + channel] / 127.5f) - 1.0f;
                }
            }
        }

        return tensor;
    }

    private OcrRecognitionResult Decode(Tensor<float> output)
    {
        if (output.Dimensions.Length != 3 || output.Dimensions[0] != 1)
        {
            throw new InvalidOperationException(
                $"Unexpected OCR output shape: {string.Join("x", output.Dimensions.ToArray())}");
        }

        var timesteps = output.Dimensions[1];
        var classCount = output.Dimensions[2];
        var text = new StringBuilder();
        var confidenceTotal = 0d;
        var confidenceCount = 0;
        var previousIndex = -1;

        for (var timestep = 0; timestep < timesteps; timestep++)
        {
            var bestIndex = 0;
            var bestProbability = float.NegativeInfinity;
            for (var classIndex = 0; classIndex < classCount; classIndex++)
            {
                var value = output[0, timestep, classIndex];
                if (value > bestProbability)
                {
                    bestProbability = value;
                    bestIndex = classIndex;
                }
            }

            if (bestIndex == previousIndex)
            {
                continue;
            }

            previousIndex = bestIndex;
            if (bestIndex == 0 || bestIndex >= _characters.Length)
            {
                continue;
            }

            var character = _characters[bestIndex];
            if (!string.IsNullOrEmpty(character))
            {
                text.Append(character);
                confidenceTotal += Math.Clamp(bestProbability, 0, 1);
                confidenceCount++;
            }
        }

        return new OcrRecognitionResult(
            text.ToString().Trim(),
            confidenceCount == 0 ? 0 : confidenceTotal / confidenceCount);
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
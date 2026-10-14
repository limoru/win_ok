using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using Automation.Core.DeveloperTools;
using Automation.Core.Frames;
using Automation.Core.Vision;
using Automation.Vision.Onnx;
using Kes.Gui.ViewModels;
using Microsoft.Win32;

namespace Kes.Gui;

public partial class DeveloperPreviewWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private PpOcrEngine? _ocrEngine;
    private Point _selectionStart;
    private Rect _selection;
    private bool _isSelecting;
    private bool _closed;

    public DeveloperPreviewWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SnapshotPageTypeBox.ItemsSource = DecisionSnapshotCatalog.All;
        UpdateImageLayout();
        ResizeWindowToContent();
        await InitializeOcrAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _ocrEngine?.Dispose();
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.DeveloperPreviewImage)
            or nameof(MainWindowViewModel.DeveloperFrameData))
        {
            UpdateImageLayout();
            ResizeWindowToContent();
            ClearSelection();
        }
    }

    private void UpdateImageLayout()
    {
        if (_viewModel.DeveloperPreviewImage is not BitmapSource image
            || image.PixelWidth <= 0
            || image.PixelHeight <= 0)
        {
            LargePreviewSurface.Width = 0;
            LargePreviewSurface.Height = 0;
            return;
        }

        var layoutWidth = image.PixelWidth;
        var layoutHeight = image.PixelHeight;

        LargePreviewImage.Width = layoutWidth;
        LargePreviewImage.Height = layoutHeight;
        LargeGridOverlay.Width = layoutWidth;
        LargeGridOverlay.Height = layoutHeight;
        LargeOverlayCanvas.Width = layoutWidth;
        MatchCandidatesCanvas.Width = layoutWidth;
        OcrBoxCanvas.Width = layoutWidth;
        LargeOverlayCanvas.Height = layoutHeight;
        MatchCandidatesCanvas.Height = layoutHeight;
        OcrBoxCanvas.Height = layoutHeight;
        LargePreviewSurface.Width = layoutWidth;
        LargePreviewSurface.Height = layoutHeight;
    }

    private void ResizeWindowToContent()
    {
        if (_viewModel.DeveloperPreviewImage is not BitmapSource image
            || image.PixelWidth <= 0
            || image.PixelHeight <= 0)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(image.PixelWidth + 400, workArea.Width * 0.97);
        Height = Math.Min(image.PixelHeight + 170, workArea.Height * 0.94);
        Left = Math.Max(workArea.Left, workArea.Left + ((workArea.Width - Width) / 2));
        Top = Math.Max(workArea.Top, workArea.Top + ((workArea.Height - Height) / 2));
    }

    private async Task InitializeOcrAsync()
    {
        ModelStatusText.Text = "正在加载 PP-OCRv5 模型...";
        var result = await Task.Run(() =>
        {
            var created = PpOcrEngine.TryCreateDefault(out var engine, out var status);
            return (Created: created, Engine: engine, Status: status);
        });

        if (_closed)
        {
            result.Engine?.Dispose();
            return;
        }

        _ocrEngine = result.Engine;
        ModelStatusText.Text = result.Status;
    }

    private void PreviewSurface_MouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(LargePreviewSurface);
        _viewModel.UpdateDeveloperPointer(
            position.X,
            position.Y,
            LargePreviewSurface.ActualWidth,
            LargePreviewSurface.ActualHeight);

        CursorVerticalLine.X1 = position.X;
        CursorVerticalLine.X2 = position.X;
        CursorVerticalLine.Y1 = 0;
        CursorVerticalLine.Y2 = LargePreviewSurface.ActualHeight;
        CursorHorizontalLine.X1 = 0;
        CursorHorizontalLine.X2 = LargePreviewSurface.ActualWidth;
        CursorHorizontalLine.Y1 = position.Y;
        CursorHorizontalLine.Y2 = position.Y;

        CursorVerticalLine.Visibility = Visibility.Visible;
        CursorHorizontalLine.Visibility = Visibility.Visible;

        if (_isSelecting)
        {
            SetSelection(NormalizeSelection(_selectionStart, ClampPoint(position)));
            e.Handled = true;
        }
    }

    private void PreviewSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        HideCursorLines();
        _viewModel.ClearDeveloperPointer();
    }

    private void PreviewSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasImage())
        {
            return;
        }

        _isSelecting = true;
        _selectionStart = ClampPoint(e.GetPosition(LargePreviewSurface));
        MatchRectangle.Visibility = Visibility.Collapsed;
        SetSelection(new Rect(_selectionStart, new Size(0, 0)));
        LargePreviewSurface.CaptureMouse();
        e.Handled = true;
    }

    private void PreviewSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        _isSelecting = false;
        LargePreviewSurface.ReleaseMouseCapture();
        var end = ClampPoint(e.GetPosition(LargePreviewSurface));
        var selection = NormalizeSelection(_selectionStart, end);
        if (selection.Width < 3 || selection.Height < 3)
        {
            ClearSelection();
        }
        else
        {
            SetSelection(selection);
            UpdateCropPreview();
        }

        e.Handled = true;
    }

    private void PreviewSurface_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ClearSelection();
        e.Handled = true;
    }

    private async void CaptureDecisionSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSourceFrame(out var frame))
        {
            DecisionSnapshotStatusText.Text = "没有可用于标注的当前帧。";
            return;
        }

        var definition = SnapshotPageTypeBox.SelectedItem as DecisionSnapshotDefinition;
        if (definition is null)
        {
            DecisionSnapshotStatusText.Text = "请选择当前画面类型。";
            return;
        }

        DecisionSnapshotStatusText.Text = "正在执行全屏 OCR 并生成标注样本...";
        OcrFrameResult? ocr = null;
        var ocrEngine = _ocrEngine;
        if (ocrEngine is not null)
        {
            try
            {
                ocr = await Task.Run(() => ocrEngine.Recognize(frame));
                DrawOcrBoxes(ocr.Lines, 0, 0);
                OcrStatusText.Text = $"全屏 OCR 完成：{ocr.Lines.Count} 行。";
            }
            catch (Exception exception)
            {
                OcrStatusText.Text = $"全屏 OCR 失败，将只保存图像和 ROI：{exception.Message}";
            }
        }

        var directory = await _viewModel.CaptureDeveloperDecisionSnapshotAsync(
            definition.ReasonId,
            ocr);
        DecisionSnapshotStatusText.Text = directory is null
            ? "标注样本生成失败，请查看开发日志。"
            : $"标注样本已保存：{directory}";
    }

    private void SaveCropButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelection(out var pixelRect)
            || _viewModel.DeveloperPreviewImage is not BitmapSource image)
        {
            OcrStatusText.Text = "请先在图片上框选要裁切的区域。";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存裁切图片",
            Filter = "PNG 图片|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = $"crop-{DateTime.Now:yyyyMMdd-HHmmss}.png"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var cropped = new CroppedBitmap(image, pixelRect);
        cropped.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropped));
        using var stream = File.Create(dialog.FileName);
        encoder.Save(stream);
        OcrStatusText.Text = $"裁切已保存：{dialog.FileName}";
    }

    private async void OcrSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelection(out var pixelRect) || !TryGetSourceFrame(out var sourceFrame))
        {
            OcrStatusText.Text = "请先在图片上框选文字区域。";
            return;
        }

        var ocrEngine = _ocrEngine;
        if (ocrEngine is null)
        {
            OcrStatusText.Text = "PP-OCRv5 模型尚未加载。";
            return;
        }

        OcrStatusText.Text = "正在识别选区...";
        OcrResultText.Text = string.Empty;
        try
        {
            var crop = sourceFrame.Crop(pixelRect.X, pixelRect.Y, pixelRect.Width, pixelRect.Height);
            var result = await Task.Run(() => ocrEngine.Recognize(crop));
            DrawOcrBoxes(result.Lines, pixelRect.X, pixelRect.Y);
            OcrResultText.Text = result.Text;
            OcrStatusText.Text = result.Lines.Count > 0
                ? $"识别完成 · {result.Lines.Count} 行 · 平均置信度 {result.Score:0.0000}"
                : "识别完成，但未检测到可识别文字。";
        }
        catch (Exception exception)
        {
            OcrStatusText.Text = $"OCR 失败：{exception.Message}";
        }
    }

    private async void FindSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelection(out var pixelRect) || !TryGetSourceFrame(out var sourceFrame))
        {
            MatchStatusText.Text = "请先在图片上框选模板区域。";
            MatchRectangle.Visibility = Visibility.Collapsed;
            MatchCandidatesCanvas.Children.Clear();
            return;
        }

        if (!TryGetMatchOptions(out var options, out var optionsError))
        {
            MatchStatusText.Text = optionsError;
            return;
        }

        MatchStatusText.Text = "正在多尺度查找...";
        MatchRectangle.Visibility = Visibility.Collapsed;
        MatchCandidatesCanvas.Children.Clear();
        try
        {
            var template = sourceFrame.Crop(pixelRect.X, pixelRect.Y, pixelRect.Width, pixelRect.Height);
            var matches = await Task.Run(() => BgraTemplateMatcher.FindMany(sourceFrame, template, options));
            if (matches.Count == 0)
            {
                MatchStatusText.Text = "未找到达到阈值的结果。";
                return;
            }

            DrawMatchCandidates(matches);
            var best = matches[0];
            Canvas.SetLeft(MatchRectangle, best.X);
            Canvas.SetTop(MatchRectangle, best.Y);
            MatchRectangle.Width = best.Width;
            MatchRectangle.Height = best.Height;
            MatchRectangle.Visibility = Visibility.Visible;

            MatchStatusText.Text =
                $"找到 {matches.Count} 个 · 最佳 ({best.X}, {best.Y}) · 缩放 {best.Scale:0.00} · 相似度 {best.Score:0.0000}";
        }
        catch (Exception exception)
        {
            MatchStatusText.Text = $"图片查找失败：{exception.Message}";
        }
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        ClearSelection();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_isSelecting)
        {
            _isSelecting = false;
            LargePreviewSurface.ReleaseMouseCapture();
            ClearSelection();
            return;
        }

        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private bool HasImage()
        => _viewModel.DeveloperPreviewImage is BitmapSource image
            && image.PixelWidth > 0
            && image.PixelHeight > 0;

    private bool TryGetSourceFrame(out BgraFrame frame)
    {
        frame = _viewModel.DeveloperFrameData!;
        return frame is not null;
    }

    private bool TryGetSelection(out Int32Rect pixelRect)
    {
        pixelRect = default;
        if (_selection.IsEmpty || _selection.Width < 1 || _selection.Height < 1)
        {
            return false;
        }

        var x = Math.Clamp((int)Math.Round(_selection.Left), 0, Math.Max(0, LargePreviewSurface.Width > 0 ? (int)LargePreviewSurface.Width - 1 : 0));
        var y = Math.Clamp((int)Math.Round(_selection.Top), 0, Math.Max(0, LargePreviewSurface.Height > 0 ? (int)LargePreviewSurface.Height - 1 : 0));
        var width = Math.Clamp((int)Math.Round(_selection.Width), 1, Math.Max(1, (int)LargePreviewSurface.Width - x));
        var height = Math.Clamp((int)Math.Round(_selection.Height), 1, Math.Max(1, (int)LargePreviewSurface.Height - y));
        pixelRect = new Int32Rect(x, y, width, height);
        return true;
    }

    private void SetSelection(Rect selection)
    {
        _selection = selection;
        if (selection.IsEmpty || selection.Width <= 0 || selection.Height <= 0)
        {
            SelectionRectangle.Visibility = Visibility.Collapsed;
            SelectionInfoText.Text = "未选择区域";
            return;
        }

        Canvas.SetLeft(SelectionRectangle, selection.Left);
        Canvas.SetTop(SelectionRectangle, selection.Top);
        SelectionRectangle.Width = selection.Width;
        SelectionRectangle.Height = selection.Height;
        SelectionRectangle.Visibility = Visibility.Visible;
        SelectionInfoText.Text = $"x={Math.Round(selection.Left)}, y={Math.Round(selection.Top)}, w={Math.Round(selection.Width)}, h={Math.Round(selection.Height)}";
    }

    private void ClearSelection()
    {
        _isSelecting = false;
        _selection = Rect.Empty;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        MatchRectangle.Visibility = Visibility.Collapsed;
        MatchCandidatesCanvas.Children.Clear();
        OcrBoxCanvas.Children.Clear();
        CropPreviewImage.Source = null;
        SelectionInfoText.Text = "未选择区域";
    }

    private void UpdateCropPreview()
    {
        if (!TryGetSelection(out var pixelRect)
            || _viewModel.DeveloperPreviewImage is not BitmapSource image)
        {
            CropPreviewImage.Source = null;
            return;
        }

        var cropped = new CroppedBitmap(image, pixelRect);
        cropped.Freeze();
        CropPreviewImage.Source = cropped;
    }

    private void DrawMatchCandidates(IReadOnlyList<TemplateMatchResult> matches)
    {
        MatchCandidatesCanvas.Children.Clear();
        var stroke = new SolidColorBrush(Color.FromArgb(210, 64, 196, 255));
        var fill = new SolidColorBrush(Color.FromArgb(22, 64, 196, 255));
        for (var index = 1; index < matches.Count; index++)
        {
            var match = matches[index];
            var rectangle = new Rectangle
            {
                Width = Math.Max(1, match.Width),
                Height = Math.Max(1, match.Height),
                Stroke = stroke,
                StrokeThickness = 1.5,
                Fill = fill,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(rectangle, match.X);
            Canvas.SetTop(rectangle, match.Y);
            MatchCandidatesCanvas.Children.Add(rectangle);
        }
    }

    private bool TryGetMatchOptions(out TemplateMatchOptions options, out string error)
    {
        options = TemplateMatchOptions.MultiScale(0.80);
        error = string.Empty;
        if (!TryParseDouble(MatchThresholdBox.Text, out var threshold)
            || !TryParseDouble(MatchMinimumScaleBox.Text, out var minimumScale)
            || !TryParseDouble(MatchMaximumScaleBox.Text, out var maximumScale)
            || !TryParseDouble(MatchScaleStepBox.Text, out var scaleStep)
            || !int.TryParse(MatchMaximumResultsBox.Text, out var maximumResults))
        {
            error = "匹配参数格式不正确。";
            return false;
        }

        if (threshold is < -1 or > 1
            || minimumScale <= 0
            || maximumScale <= 0
            || scaleStep <= 0.001
            || maximumResults < 1)
        {
            error = "阈值、缩放范围或结果数超出有效范围。";
            return false;
        }

        options = new TemplateMatchOptions(
            threshold,
            minimumScale,
            maximumScale,
            scaleStep,
            Math.Clamp(maximumResults, 1, 100));
        return true;
    }

    private static bool TryParseDouble(string text, out double value)
    {
        return double.TryParse(
                   text,
                   NumberStyles.Float,
                   CultureInfo.CurrentCulture,
                   out value)
               || double.TryParse(
                   text,
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out value);
    }
    private void DrawOcrBoxes(IReadOnlyList<OcrTextLine> lines, int offsetX, int offsetY)
    {
        OcrBoxCanvas.Children.Clear();
        var stroke = new SolidColorBrush(Color.FromRgb(74, 222, 128));
        var fill = new SolidColorBrush(Color.FromArgb(28, 74, 222, 128));
        foreach (var line in lines)
        {
            var rectangle = new Rectangle
            {
                Width = Math.Max(1, line.Region.Width),
                Height = Math.Max(1, line.Region.Height),
                Stroke = stroke,
                StrokeThickness = 2,
                Fill = fill,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(rectangle, offsetX + line.Region.X);
            Canvas.SetTop(rectangle, offsetY + line.Region.Y);
            OcrBoxCanvas.Children.Add(rectangle);
        }
    }
    private Point ClampPoint(Point point)
    {
        var width = Math.Max(0, LargePreviewSurface.ActualWidth);
        var height = Math.Max(0, LargePreviewSurface.ActualHeight);
        return new Point(
            Math.Clamp(point.X, 0, width),
            Math.Clamp(point.Y, 0, height));
    }

    private static Rect NormalizeSelection(Point start, Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        return new Rect(left, top, Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
    }

    private void HideCursorLines()
    {
        CursorVerticalLine.Visibility = Visibility.Collapsed;
        CursorHorizontalLine.Visibility = Visibility.Collapsed;
    }
}

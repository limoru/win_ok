using Automation.Core.Arbitration;
using Automation.Core.Targeting;
using Automation.Core.Vision;

namespace Kes.Tasks;

public sealed partial class ChaosModeTask
{
    private sealed class ChaosModeFrameClassification
    {
        private readonly ChaosModePage _page;
        private readonly Dictionary<(string Name, RelativeRegion? Region, double? Threshold), IReadOnlyList<FeatureBox>> _features = [];
        private readonly Dictionary<(double X, double Y), OcrTextLine?> _boxesAtPoint = [];
        private readonly Dictionary<RelativeRegion, IReadOnlyList<OcrTextLine>> _boxesByRegion = [];
        private IReadOnlyList<EventOption>? _eventOptions;
        private IReadOnlyList<RouteNode>? _routeNodes;
        private IReadOnlyList<ChaosCardInfo>? _cards;

        public ChaosModeFrameClassification(ChaosModePage page)
        {
            _page = page;
        }

        public OcrTextLine? FindBoxAtPoint(double relativeX, double relativeY)
        {
            var key = (relativeX, relativeY);
            if (_boxesAtPoint.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var result = FindBoxAtPointCore(relativeX, relativeY);
            _boxesAtPoint[key] = result;
            return result;
        }

        public IReadOnlyList<FeatureBox> FindFeatures(
            string featureName,
            RelativeRegion? region = null,
            double? threshold = null)
        {
            var key = (featureName, region, threshold);
            if (_features.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var matcher = _page.Context.FeatureMatcher;
            if (matcher is null)
            {
                _features[key] = [];
                return _features[key];
            }

            var searchRegion = region is null
                ? null
                : new FeatureSearchRegion(
                    region.Value.Left,
                    region.Value.Top,
                    region.Value.Right,
                    region.Value.Bottom);
            var result = matcher
                .Find(_page.Frame, featureName, searchRegion, threshold)
                .ToArray();
            _features[key] = result;
            return result;
        }

        public IReadOnlyList<OcrTextLine> FindOcrBoxes(RelativeRegion region)
        {
            if (_boxesByRegion.TryGetValue(region, out var cached))
            {
                return cached;
            }

            var result = _page.Recognized.Lines
                .Where(line => region.Contains(line, _page.Frame.Width, _page.Frame.Height))
                .ToArray();
            _boxesByRegion[region] = result;
            return result;
        }

        public IReadOnlyList<EventOption> EventOptions => _eventOptions ??= CreateEventOptions();

        public IReadOnlyList<RouteNode> RouteNodes => _routeNodes ??= CreateRouteNodes();

        public IReadOnlyList<ChaosCardInfo> Cards
        {
            get
            {
                if (_cards is not null)
                {
                    return _cards;
                }

                var matcher = _page.Context.FeatureMatcher;
                _cards = matcher is null
                    ? []
                    : ChaosCardRecognition
                        .Recognize(_page.Frame, _page.Recognized, matcher)
                        .ToArray();
                return _cards;
            }
        }

        private OcrTextLine? FindBoxAtPointCore(double relativeX, double relativeY)
        {
            if (_page.Frame.Width <= 0 || _page.Frame.Height <= 0)
            {
                return null;
            }

            var pixelX = relativeX * _page.Frame.Width;
            var pixelY = relativeY * _page.Frame.Height;
            OcrTextLine? best = null;
            foreach (var line in _page.Recognized.Lines)
            {
                var region = line.Region;
                if (pixelX < region.X
                    || pixelX > region.X + region.Width
                    || pixelY < region.Y
                    || pixelY > region.Y + region.Height)
                {
                    continue;
                }

                if (best is null
                    || (long)region.Width * region.Height
                       < (long)best.Region.Width * best.Region.Height)
                {
                    best = line;
                }
            }

            return best;
        }

        private IReadOnlyList<EventOption> CreateEventOptions()
        {
            var candidates = new List<EventFeature>();
            foreach (var featureName in EventFeatureNames)
            {
                foreach (var box in FindFeatures(featureName, EventOptionsRegion, 0.80))
                {
                    candidates.Add(new EventFeature(
                        featureName,
                        box.CenterX(_page.Frame.Width),
                        box.CenterY(_page.Frame.Height),
                        box.Confidence));
                }
            }

            var filtered = new List<EventFeature>(3);
            foreach (var candidate in candidates.OrderByDescending(item => item.Confidence))
            {
                if (filtered.Any(kept =>
                        Math.Sqrt(
                            Math.Pow(candidate.CenterX - kept.CenterX, 2)
                            + Math.Pow(candidate.CenterY - kept.CenterY, 2)) < 0.207))
                {
                    continue;
                }

                filtered.Add(candidate);
                if (filtered.Count >= 3)
                {
                    break;
                }
            }

            filtered.Sort((left, right) => left.CenterX.CompareTo(right.CenterX));
            var options = new List<EventOption>(filtered.Count);
            foreach (var candidate in filtered)
            {
                var descriptionRegion = new RelativeRegion(
                    Math.Clamp(candidate.CenterX - 0.119, 0, 1),
                    Math.Clamp(candidate.CenterY - 0.208, 0, 1),
                    Math.Clamp(candidate.CenterX + 0.122, 0, 1),
                    Math.Clamp(candidate.CenterY - 0.021, 0, 1));
                var description = RegionText(_page, descriptionRegion).Trim();
                if (description.Length == 0 || !ContainsCjk(description))
                {
                    continue;
                }

                var clickX = (descriptionRegion.Left + descriptionRegion.Right) / 2;
                var clickY = (descriptionRegion.Top + descriptionRegion.Bottom) / 2;
                options.Add(new EventOption(
                    candidate.FeatureName,
                    description,
                    candidate.CenterX,
                    candidate.CenterY,
                    new RelativePoint(clickX, clickY),
                    candidate.Confidence));
            }

            if (options.Count > 0)
            {
                _page.Context.LogInfo($"事件任务页面: 识别到{options.Count}个事件选项。");
                foreach (var option in options)
                {
                    _page.Context.LogInfo(
                        $"事件选项: 描述=「{option.Description}」，特征={option.FeatureName}，置信度={option.Confidence:0.0000}。");
                }
            }

            return options;
        }

        private IReadOnlyList<RouteNode> CreateRouteNodes()
        {
            var routeRegion = new RelativeRegion(0.656, 0.053, 0.977, 0.908);
            var nodes = new List<RouteNode>();
            foreach (var (featureName, nodeType) in new[]
                     {
                         ("safezone", "休息"),
                         ("enemy", "小怪"),
                         ("elite", "精英"),
                         ("event", "事件"),
                         ("settlement", "结算")
                     })
            {
                foreach (var box in FindFeatures(featureName, routeRegion))
                {
                    nodes.Add(new RouteNode(featureName, nodeType, box, []));
                }
            }

            return nodes;
        }
    }
}
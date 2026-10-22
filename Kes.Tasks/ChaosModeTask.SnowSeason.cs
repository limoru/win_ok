using System.Text.RegularExpressions;
using Automation.Core.Configuration;
using Automation.Core.Runtime;
using Automation.Core.Targeting;
using Automation.Core.Vision;
using Kes.Tasks.Configuration;

namespace Kes.Tasks;

public enum SnowSeasonPhase
{
    None,
    PlanMemoryChoice,
    PlanCardSelected,
    PlanMemberChoice,
    PrecisionEventSelected,
    PrecisionCardChoice,
    PrecisionConfirmPending
}

public sealed partial class ChaosModeTask
{
    private const int SnowMemorySlotLimit = 3;
    private const int SnowMemoryRefreshLimit = 3;
    private static readonly TimeSpan SnowActionResponseTimeout = TimeSpan.FromSeconds(3);
    private static readonly RelativeRegion SnowEventTitleRegion = new(0.080, 0.000, 0.920, 0.220);
    private static readonly RelativeRegion[] SnowMemoryFallbackRegions =
    [
        new(0.045, 0.140, 0.350, 0.820),
        new(0.350, 0.140, 0.650, 0.820),
        new(0.650, 0.140, 0.955, 0.820)
    ];

    private sealed record SnowMemoryCandidate(
        int Index,
        string OcrText,
        RelativePoint ClickPoint,
        OcrTextLine? RefreshLine,
        int RefreshRemaining,
        RelativeRegion Region);

    private static bool IsSnowboundPromiseSeason(ChaosModePage page)
        => string.Equals(
            page.Context.GetTaskConfig(
                ChaosModeConfigSchema.SeasonSelectorKey,
                ChaosModeConfigSchema.NoSeasonContent),
            ChaosModeConfigSchema.SnowboundPromiseSeason,
            StringComparison.Ordinal);

    private static bool IsSnowSeasonPage(ChaosModePage page)
    {
        if (!IsSnowboundPromiseSeason(page) || FindCenterConfirmation(page) is not null)
        {
            return false;
        }

        return IsSnowWhiteMemoryPage(page)
               || IsSnowMemorySelectionPage(page)
               || IsSnowMemberSelectionPage(page)
               || IsSnowPrecisionActionPage(page)
               || IsSnowMemoryEndPage(page)
               || IsSnowEventWithoutIconPage(page);
    }

    private static string ResolveSnowSeasonActionStage(ChaosModePage page)
    {
        if (IsSnowWhiteMemoryPage(page))
        {
            return $"snow-white-memory-{page.Observation.Fingerprint.Value}";
        }

        if (IsSnowEventWithoutIconPage(page))
        {
            return $"snow-event-no-icon-{page.Observation.Fingerprint.Value}";
        }

        if (IsSnowMemberSelectionPage(page))
        {
            return "snow-member";
        }

        if (IsSnowMemorySelectionPage(page))
        {
            return IsSnowPrecisionMemoryPage(page)
                ? "snow-precision-card"
                : "snow-plan-card";
        }

        if (IsSnowPrecisionActionPage(page))
        {
            return "snow-precision-action";
        }

        if (IsSnowMemoryEndPage(page))
        {
            return "snow-memory-end";
        }

        return "snow-season";
    }

    private static async ValueTask<bool> HandleSnowSeasonAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        if (!IsSnowboundPromiseSeason(page))
        {
            return false;
        }

        if (await HandleSnowWhiteMemoryAsync(page, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (await HandleSnowMemorySelectionAsync(page, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (await HandleSnowMemberSelectionAsync(page, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (await HandleSnowPrecisionActionAsync(page, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (await HandleSnowMemoryEndAsync(page, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return await HandleSnowEventWithoutIconAsync(page, cancellationToken).ConfigureAwait(false);
    }

    private static EventOption? FindSnowSeasonPriorityEventOption(
        ChaosModePage page,
        out SnowSeasonPhase phase)
    {
        phase = SnowSeasonPhase.None;
        if (!IsSnowboundPromiseSeason(page))
        {
            return null;
        }

        var options = RecognizeEventOptions(page);
        var planInstruction = options.FirstOrDefault(option =>
            ContainsSnowText(page, option.Description, "领受计划的指示"));
        if (planInstruction is not null)
        {
            phase = SnowSeasonPhase.PlanMemoryChoice;
            return planInstruction;
        }

        var precision = options.FirstOrDefault(option =>
            ContainsSnowText(page, option.Description, "精密加工记忆"));
        if (precision is not null)
        {
            phase = SnowSeasonPhase.PrecisionEventSelected;
            return precision;
        }

        return null;
    }

    private static string? ResolveSnowSeasonEventSelectionKey(EventOption option)
    {
        var description = option.Description.Replace(" ", string.Empty, StringComparison.Ordinal);
        return description.Contains("领受计划的指示", StringComparison.Ordinal)
            || description.Contains("傾受計劃的指示", StringComparison.Ordinal)
            ? "领受计划的指示"
            : description.Contains("精密加工记忆", StringComparison.Ordinal)
                ? "精密加工记忆"
                : null;
    }

    private static void ResetSnowMemoryAttempts(ChaosRunState state)
    {
        state.SnowMemoryRefreshAttempts.Clear();
        state.SnowMemoryRefreshAttempts.AddRange(Enumerable.Repeat(0, SnowMemorySlotLimit));
        state.SnowSelectedMemoryName = string.Empty;
    }

    private static bool IsSnowWhiteMemoryPage(ChaosModePage page)
        => FindSnowLine(page, "纯白的记忆") is not null;

    private static bool IsSnowMemorySelectionPage(ChaosModePage page)
    {
        if (FindSnowLine(page, "选择铭刻的记忆") is not null)
        {
            return true;
        }

        return page.State.SnowSeasonPhase == SnowSeasonPhase.PlanCardSelected
               && FindActiveFooterConfirmBox(page) is not null;
    }

    private static bool IsSnowPrecisionMemoryPage(ChaosModePage page)
        => page.State.SnowSeasonPhase == SnowSeasonPhase.PrecisionCardChoice
           || (FindSnowLine(page, "选择铭刻的记忆") is not null
               && FindSnowLine(page, "加工记忆") is not null);

    private static bool IsSnowMemberSelectionPage(ChaosModePage page)
    {
        if (page.State.SnowSeasonPhase != SnowSeasonPhase.PlanMemberChoice)
        {
            return false;
        }

        return FindSnowLine(page, "主战员") is not null
               || FindActiveFooterConfirmBox(page) is not null
               || FindFeatureOne(page, "target_member_large") is not null;
    }

    private static bool IsSnowPrecisionActionPage(ChaosModePage page)
    {
        return FindSnowLine(page, "选择铭刻的记忆") is null
               && RecognizeEventOptions(page).Count == 0
               && (FindSnowActionButtonLine(page, "加工记忆", "精密加工记忆") is not null
                   || FindSnowLine(page, "精密加工记忆") is not null);
    }

    private static bool IsSnowMemoryEndPage(ChaosModePage page)
        => FindSnowLine(page, "前往记忆之末") is not null;

    private static bool IsSnowEventWithoutIconPage(ChaosModePage page)
    {
        if (!IsSnowboundPromiseSeason(page)
            || RecognizeEventOptions(page).Count > 0
            || FindFeatureOne(page, "taskreward") is not null
            || EventFeatureNames.Any(name => FindFeatureOne(page, name, EventOptionsRegion) is not null))
        {
            return false;
        }

        var title = RegionText(page, SnowEventTitleRegion);
        return ContainsSnowText(page, title, "事件")
               || FindSnowLine(page, "铭刻的记忆") is not null;
    }

    private static async ValueTask<bool> HandleSnowWhiteMemoryAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindSnowLine(page, "纯白的记忆");
        if (line is null)
        {
            return false;
        }

        page.Context.LogInfo("检测到纯白的记忆，点击画面跳过。", "activity");
        await ClickPointAsync(
                page,
                new RelativePoint(0.5, 0.5),
                "点击纯白的记忆画面跳过",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static async ValueTask<bool> HandleSnowMemorySelectionAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        if (!IsSnowMemorySelectionPage(page))
        {
            return false;
        }

        var precision = IsSnowPrecisionMemoryPage(page);
        var state = page.State;
        if (precision)
        {
            state.SnowSeasonPhase = SnowSeasonPhase.PrecisionCardChoice;
        }
        else if (state.SnowSeasonPhase is SnowSeasonPhase.None or SnowSeasonPhase.PlanMemoryChoice)
        {
            state.SnowSeasonPhase = SnowSeasonPhase.PlanMemoryChoice;
        }

        if (!precision
            && state.SnowSeasonPhase is SnowSeasonPhase.PlanCardSelected or SnowSeasonPhase.PlanMemberChoice)
        {
            var confirm = FindActiveFooterConfirmBox(page);
            if (confirm is null)
            {
                page.Context.LogDebug("铭刻记忆卡牌已选择，等待确认按钮或主战员页面出现。");
                return true;
            }

            if (state.SnowSeasonPhase == SnowSeasonPhase.PlanMemberChoice
                && HasRecentSnowInput(state))
            {
                page.Context.LogDebug("铭刻记忆确认已下发，等待主战员选择页面。");
                return true;
            }

            await ClickFooterConfirmAsync(
                    page,
                    confirm,
                    "确认选择的铭刻记忆",
                    cancellationToken)
                .ConfigureAwait(false);
            state.SnowSeasonPhase = SnowSeasonPhase.PlanMemberChoice;
            MarkSnowInput(state);
            return true;
        }

        var candidates = ReadSnowMemoryCandidates(page, includeRefreshButtons: !precision);
        if (candidates.Count == 0)
        {
            page.Context.LogInfo("铭刻记忆页面未识别到候选卡牌，等待下一帧。", "warning");
            return true;
        }

        LogSnowMemoryCandidates(page, precision, candidates);
        var recognizedCandidates = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.OcrText))
            .ToArray();
        if (recognizedCandidates.Length == 0)
        {
            page.Context.LogInfo("铭刻记忆候选卡牌文字识别为空，等待下一帧。", "warning");
            return true;
        }

        var priorityKey = precision
            ? ChaosModeConfigSchema.SnowPrecisionMemoryPriorityKey
            : ChaosModeConfigSchema.SnowMemoryPriorityKey;
        var chosen = FindConfiguredSnowMemoryCandidate(
            page,
            recognizedCandidates,
            page.Context.Config.GetStringList(priorityKey));
        if (chosen is not null)
        {
            await SelectSnowMemoryCandidateAsync(
                    page,
                    chosen,
                    precision,
                    $"命中配置「{priorityKey}」",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (!precision)
        {
            var refreshCandidates = candidates
                .Where(candidate => candidate.RefreshLine is not null)
                .ToArray();
            if (refreshCandidates.Length < SnowMemorySlotLimit)
            {
                page.Context.LogInfo(
                    $"铭刻记忆仅识别到{refreshCandidates.Length}/{SnowMemorySlotLimit}个重新搜索按钮，等待下一帧完整识别。",
                    "warning");
                return true;
            }

            EnsureSnowMemoryAttempts(state);
            foreach (var candidate in refreshCandidates)
            {
                var refreshLine = candidate.RefreshLine;
                if (refreshLine is null)
                {
                    continue;
                }

                if (candidate.RefreshRemaining <= 0
                    || state.SnowMemoryRefreshAttempts[candidate.Index] >= SnowMemoryRefreshLimit)
                {
                    continue;
                }

                state.SnowMemoryRefreshAttempts[candidate.Index]++;
                MarkSnowInput(state);
                page.Context.LogInfo(
                    $"铭刻记忆未命中配置，点击第{candidate.Index + 1}张卡牌的重新搜索（{state.SnowMemoryRefreshAttempts[candidate.Index]}/{SnowMemoryRefreshLimit}）。",
                    "activity");
                await ClickTextAsync(
                        page,
                        refreshLine,
                        $"重新搜索第{candidate.Index + 1}张铭刻记忆",
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
        }

        chosen = recognizedCandidates[Random.Shared.Next(recognizedCandidates.Length)];
        await SelectSnowMemoryCandidateAsync(
                page,
                chosen,
                precision,
                precision ? "加工记忆未命中配置，随机选择" : "重新搜索耗尽后随机选择",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static async ValueTask SelectSnowMemoryCandidateAsync(
        ChaosModePage page,
        SnowMemoryCandidate candidate,
        bool precision,
        string reason,
        CancellationToken cancellationToken)
    {
        await ClickPointAsync(
                page,
                candidate.ClickPoint,
                $"选择铭刻记忆「{candidate.OcrText}」：{reason}",
                cancellationToken)
            .ConfigureAwait(false);
        page.State.SnowSelectedMemoryName = candidate.OcrText;
        page.State.SnowSeasonPhase = precision
            ? SnowSeasonPhase.PrecisionEventSelected
            : SnowSeasonPhase.PlanCardSelected;
        MarkSnowInput(page.State);
    }

    private static async ValueTask<bool> HandleSnowMemberSelectionAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        if (!IsSnowMemberSelectionPage(page))
        {
            return false;
        }

        var state = page.State;
        var now = ResolveOperationTimestamp(page);
        var activeConfirm = FindActiveFooterConfirmBox(page);
        var candidatePending = state.SnowMemberCandidateClickedAt != default
                               && now - state.SnowMemberCandidateClickedAt
                               < OwnedFooterConfirmationRetryInterval;
        if (activeConfirm is not null && state.SnowMemberAttemptIndex > 0)
        {
            state.SnowMemberAttemptIndex = 0;
            state.SnowMemberCandidateClickedAt = default;
            state.SnowMemberConfirmClickedAt = now;
            state.SnowSeasonPhase = SnowSeasonPhase.None;
            MarkSnowInput(state);
            await ClickFooterConfirmAsync(
                    page,
                    activeConfirm,
                    "确认铭刻记忆主战员",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (candidatePending)
        {
            page.Context.LogDebug("铭刻记忆主战员选择已下发，等待确认按钮。");
            return true;
        }

        var clickPositions = PrioritizeTargetMemberClicks(page, out _);

        if (clickPositions.Count == 0)
        {
            page.Context.LogInfo("铭刻记忆主战员页面没有可点击候选，等待下一帧。", "warning");
            return true;
        }

        var attemptIndex = state.SnowMemberAttemptIndex;

        if (attemptIndex < 0 || attemptIndex >= clickPositions.Count)
        {
            state.SnowMemberAttemptIndex = 0;
            state.SnowMemberCandidateClickedAt = default;
            page.Context.LogInfo("铭刻记忆主战员候选均已尝试，等待页面变化。", "warning");
            return true;
        }

        var position = clickPositions[attemptIndex];
        state.SnowMemberAttemptIndex = attemptIndex + 1;
        state.SnowMemberCandidateClickedAt = now;
        MarkSnowInput(state);
        await ClickPointAsync(
                page,
                position,
                "选择铭刻记忆主战员",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static async ValueTask<bool> HandleSnowPrecisionActionAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        if (!IsSnowPrecisionActionPage(page))
        {
            return false;
        }

        var state = page.State;
        if (state.SnowSeasonPhase == SnowSeasonPhase.PrecisionConfirmPending
            && HasRecentSnowInput(state))
        {
            page.Context.LogDebug("精密加工记忆已下发，等待确认或关闭页面。");
            return true;
        }

        var processing = FindSnowActionButtonLine(page, "加工记忆", "精密加工记忆");
        if (processing is not null)
        {
            state.SnowSeasonPhase = SnowSeasonPhase.PrecisionCardChoice;
            MarkSnowInput(state);
            page.Context.LogInfo("检测到加工记忆入口，先执行本次加工。", "activity");
            await ClickTextAsync(
                    page,
                    processing,
                    "点击加工记忆",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        var precision = FindSnowLine(page, "精密加工记忆");
        if (precision is null)
        {
            return false;
        }

        state.SnowSeasonPhase = SnowSeasonPhase.PrecisionConfirmPending;
        MarkSnowInput(state);
        page.Context.LogInfo("检测到精密加工记忆按钮，提交精密加工。", "activity");
        await ClickTextAsync(
                page,
                precision,
                "点击精密加工记忆",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static async ValueTask<bool> HandleSnowMemoryEndAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindSnowLine(page, "前往记忆之末");
        if (line is null)
        {
            return false;
        }

        page.Context.LogInfo("检测到前往记忆之末，按继续操作处理。", "activity");
        await ClickTextAsync(page, line, "前往记忆之末", cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static async ValueTask<bool> HandleSnowEventWithoutIconAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        if (!IsSnowEventWithoutIconPage(page))
        {
            return false;
        }

        var fingerprint = page.Observation.Fingerprint.Value;
        if (string.Equals(
                page.State.SnowEventFallbackFingerprint,
                fingerprint,
                StringComparison.Ordinal))
        {
            page.Context.LogDebug("当前无图标事件已按过 F1，等待页面变化。");
            return true;
        }

        if (page.Context.UiProfile != UiProfile.WindowsPc)
        {
            page.Context.LogInfo("无图标事件仅支持 F1 处理，当前平台忽略。", "warning");
            return false;
        }

        page.State.SnowEventFallbackFingerprint = fingerprint;
        page.Context.LogInfo("检测到无图标事件，按 F1 继续。", "activity");
        await PressKeyAsync(
                page,
                InputKey.F1,
                "无图标事件按 F1",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static IReadOnlyList<SnowMemoryCandidate> ReadSnowMemoryCandidates(
        ChaosModePage page,
        bool includeRefreshButtons)
    {
        var lines = page.Recognized.Lines;
        if (includeRefreshButtons)
        {
            var refreshLines = new List<OcrTextLine>();
            foreach (var line in lines
                         .Where(line => ContainsSnowText(page, line.Text, "重新搜索"))
                         .OrderBy(line => line.Region.X + (line.Region.Width / 2d)))
            {
                var x = line.Region.X + (line.Region.Width / 2d);
                if (refreshLines.Any(existing =>
                        Math.Abs(
                            (existing.Region.X + (existing.Region.Width / 2d)) - x)
                        < page.Frame.Width * 0.10))
                {
                    continue;
                }

                refreshLines.Add(line);
                if (refreshLines.Count >= SnowMemorySlotLimit)
                {
                    break;
                }
            }

            if (refreshLines.Count > 0)
            {
                return CreateSnowMemoryCandidatesFromRefreshButtons(page, lines, refreshLines);
            }
        }

        var fallback = new List<SnowMemoryCandidate>(SnowMemorySlotLimit);
        for (var index = 0; index < SnowMemoryFallbackRegions.Length; index++)
        {
            var region = SnowMemoryFallbackRegions[index];
            var regionLines = lines
                .Where(line => region.Contains(line, page.Frame.Width, page.Frame.Height))
                .Where(line => ContainsCjk(line.Text))
                .OrderByDescending(line => line.Score)
                .ThenBy(line => (long)line.Region.Width * line.Region.Height)
                .ToArray();
            if (regionLines.Length == 0)
            {
                continue;
            }

            var clickLine = regionLines.FirstOrDefault();
            var clickPoint = clickLine is null
                ? new RelativePoint((region.Left + region.Right) / 2, (region.Top + region.Bottom) / 2)
                : new RelativePoint(
                    (clickLine.Region.X + (clickLine.Region.Width / 2d)) / page.Frame.Width,
                    (clickLine.Region.Y + (clickLine.Region.Height / 2d)) / page.Frame.Height);
            fallback.Add(new SnowMemoryCandidate(
                index,
                string.Concat(regionLines.Select(line => line.Text.Trim())),
                clickPoint.Clamp(),
                null,
                SnowMemoryRefreshLimit,
                region));
        }

        return fallback;
    }

    private static IReadOnlyList<SnowMemoryCandidate> CreateSnowMemoryCandidatesFromRefreshButtons(
        ChaosModePage page,
        IReadOnlyList<OcrTextLine> lines,
        IReadOnlyList<OcrTextLine> refreshLines)
    {
        var centers = refreshLines
            .Select(line => line.Region.X + (line.Region.Width / 2d))
            .ToArray();
        var candidates = new List<SnowMemoryCandidate>(refreshLines.Count);
        for (var index = 0; index < refreshLines.Count; index++)
        {
            var refresh = refreshLines[index];
            var refreshX = centers[index];
            var refreshY = (refresh.Region.Y + (refresh.Region.Height / 2d)) / page.Frame.Height;
            var left = index == 0 ? 0.02 : (centers[index - 1] + refreshX) / 2d / page.Frame.Width;
            var right = index == centers.Length - 1
                ? 0.98
                : (centers[index + 1] + refreshX) / 2d / page.Frame.Width;
            var region = new RelativeRegion(left, 0.120, right, Math.Max(0.121, refreshY));
            var cardLines = lines
                .Where(line => region.Contains(line, page.Frame.Width, page.Frame.Height))
                .Where(line => !string.Equals(line.Text, refresh.Text, StringComparison.Ordinal))
                .Where(line => ContainsCjk(line.Text))
                .OrderByDescending(line => line.Score)
                .ThenBy(line => Math.Abs(
                    (line.Region.Y + (line.Region.Height / 2d)) / page.Frame.Height
                    - Math.Max(0.120, refreshY - 0.220)))
                .ToArray();
            if (cardLines.Length == 0)
            {
                continue;
            }

            var clickLine = cardLines.FirstOrDefault();
            var clickPoint = clickLine is null
                ? new RelativePoint(refreshX / page.Frame.Width, Math.Clamp(refreshY - 0.220, 0, 1))
                : new RelativePoint(
                    (clickLine.Region.X + (clickLine.Region.Width / 2d)) / page.Frame.Width,
                    (clickLine.Region.Y + (clickLine.Region.Height / 2d)) / page.Frame.Height);
            candidates.Add(new SnowMemoryCandidate(
                index,
                string.Concat(cardLines.Select(line => line.Text.Trim())),
                clickPoint.Clamp(),
                refresh,
                ParseSnowRefreshRemaining(refresh.Text),
                region));
        }

        return candidates;
    }

    private static int ParseSnowRefreshRemaining(string text)
    {
        var match = Regex.Match(text, @"(\d+)\s*/\s*(\d+)", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var remaining)
            ? Math.Clamp(remaining, 0, SnowMemoryRefreshLimit)
            : SnowMemoryRefreshLimit;
    }

    private static SnowMemoryCandidate? FindConfiguredSnowMemoryCandidate(
        ChaosModePage page,
        IReadOnlyList<SnowMemoryCandidate> candidates,
        IReadOnlyList<string> priorities)
    {
        foreach (var priority in priorities.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var normalizedPriority = NormalizeSnowMatchText(priority);
            if (normalizedPriority.Length == 0)
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                var normalizedText = NormalizeSnowMatchText(candidate.OcrText);
                if (normalizedText.Contains(normalizedPriority, StringComparison.Ordinal)
                    || ChaosCardRecognition.IsSubsequence(priority, candidate.OcrText))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static void EnsureSnowMemoryAttempts(ChaosRunState state)
    {
        while (state.SnowMemoryRefreshAttempts.Count < SnowMemorySlotLimit)
        {
            state.SnowMemoryRefreshAttempts.Add(0);
        }
    }

    private static void LogSnowMemoryCandidates(
        ChaosModePage page,
        bool precision,
        IReadOnlyList<SnowMemoryCandidate> candidates)
    {
        page.Context.LogDebug(
            $"{(precision ? "精密加工记忆" : "铭刻记忆")}候选："
            + string.Join(" | ", candidates.Select(candidate =>
                $"#{candidate.Index + 1}「{candidate.OcrText}」剩余搜索={candidate.RefreshRemaining}")));
    }

    private static OcrTextLine? FindSnowActionButtonLine(
        ChaosModePage page,
        string includedTerm,
        string excludedTerm)
        => page.Recognized.Lines
            .Where(line => ContainsSnowText(page, line.Text, includedTerm))
            .Where(line => !ContainsSnowText(page, line.Text, excludedTerm))
            .OrderByDescending(line => line.Score)
            .FirstOrDefault();

    private static OcrTextLine? FindSnowLine(ChaosModePage page, string simplified)
        => page.Recognized.Lines.FirstOrDefault(line =>
            ContainsSnowText(page, line.Text, simplified));

    private static bool ContainsSnowText(ChaosModePage page, string actual, string simplified)
        => GameText(page, simplified)
            .Any(expected => actual.Contains(expected, StringComparison.Ordinal));

    private static string NormalizeSnowMatchText(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static bool HasRecentSnowInput(ChaosRunState state)
        => state.SnowSeasonLastInputAt != default
           && DateTimeOffset.Now - state.SnowSeasonLastInputAt < SnowActionResponseTimeout;

    private static void MarkSnowInput(ChaosRunState state)
    {
        state.SnowSeasonInputVersion++;
        state.SnowSeasonLastInputAt = DateTimeOffset.Now;
    }
}

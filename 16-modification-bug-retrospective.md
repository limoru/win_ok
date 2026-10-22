# 16. 修改引入缺陷反思记录

> 记录日期：2026-10-17
>
> 范围：从 Windows C# 版本开始实现，到当前卡厄思模式迁移、帧总线、局部 OCR 和开发测试功能为止。
>
> 收录标准：有代码、自动化测试、运行日志、真实 OCR 文本或用户实机复现作为依据的修改引入缺陷。单纯的功能缺失、尚未开始的刻印/面具/赛季迁移，以及已经确认的审查误判不计入 BUG 数量。

## 1. 反思结论

这些缺陷反复出现，主要原因不是某几个按钮坐标写错，而是修改时没有先确认问题属于哪一层：

1. 把通用生命周期写进页面专用状态。
2. 用 OCR 文案存在代替按钮可执行。
3. 用固定坐标、固定等待或固定帧数掩盖识别和时序问题。
4. 把总线、消费者、页面状态机和输入动作混在同一条等待链里。
5. 只修复用户指出的一个页面，没有检查同类页面。
6. 只增加正向测试，没有覆盖按钮禁用、页面未离开、旧帧、取消、超时和消费者拒绝。
7. 修改了数据结构或行为，却没有同步 UI 文案、迁移台账和回归测试。
8. 为了“让流程继续”自行增加需求，例如三帧确认、普通点击长按，而这些需求并不存在。

固定结论：

> 页面数据和游戏文案可以页面化；动作生命周期、确认、去重、超时、取消和重试必须对象化并共享。

> 总线只负责“这一帧应该投递给谁”，不能因为消费者忙碌、旧动作未释放或 OCR 文本相同而停止投递。

> 路由命中不等于动作成功；按钮文字存在不等于按钮可点击；测试文本框存在不等于真实画面有这个按钮。

本文当前记录 63 项：已修复 61 项，部分完成 1 项，待修复 0 项，待同步 1 项。未迁移功能和功能缺失不计入该数量。

## 2. 状态、路由和业务动作缺陷

| ID | 修改引入的缺陷 | 根因 | 当前修复与回归保护 | 状态 |
|---|---|---|---|---|
| RB-01 | 卡牌跳过使用页面专用 Requested/Confirmed 布尔值，休息、事件等页面又复制同类逻辑，出现重复跳过和确认循环。 | 通用确认流程被硬编码到单个页面。 | 改为共享确认会话和动作所有权，详见 12-design-errors-and-guardrails.md 的 ERR-001。 | 已修复 |
| RB-02 | 主战员头像采集期间，事件处理器抢先选择事件选项，日志表现为“先读取事件选项，再寻找主战员”。 | 跨页面采集依赖处理器顺序和临时阶段字段，没有独占流程对象。 | 使用 TargetMemberArchiveFlow 独占流程；有流程激活、超时和恢复测试。 | 已修复 |
| RB-03 | 休息点击后持续循环“点击休息 -> 点击确认 -> 再点击休息”。 | 迁移时把 Python 的业务门闩 AND 条件改成了带文字兜底的宽松条件，已消费节点被重新激活。 | 恢复“休息特征 + 免费文字 + FlashOrRest”同时成立；RestDoesNotClickWhenFreeTextMissing 等测试覆盖。 | 已修复 |
| RB-04 | 事件、卡牌、商店等普通页面可能在同一语义页面上重复执行动作。 | 普通路由没有统一页面动作所有权，页面各自维护是否点过。 | 接入 PageActionSession，按唯一路由和页面语义申请动作权。 | 已修复 |
| RB-05 | 闪光效果页已经识别到路由，但卡牌识别失败后返回 false，后续跳过路由仍无法执行，页面永久空转。 | 路由框架把“命中”误当成“动作成功”，高优先级路由吞掉了后续兜底路由。 | 增加显式 AllowFallbackWhenUnhandled 语义；闪光页未识别时保存诊断并继续尝试后续路由。 | 已修复 |
| RB-06 | 点击后仍处理点击前截取的旧帧，导致退出、确认或其他动作被重复执行。 | 帧只按序号推进，没有校验截图时间相对输入完成时间是否新鲜。 | FrameFreshnessTests.NextFrameAfterInputDiscardsFramesCapturedBeforeClickCompletion 锁定点击后必须丢弃旧帧。 | 已修复 |
| RB-07 | 打开逃脱页面后，即使没有下发逃脱指令，也可能继续逃脱并结束本局。 | 页面特征被当成了业务命令，缺少显式命令所有权。 | 增加 ChaosCommandBus；无指令时取消逃脱页面并返回，不设置 IsEscaped。 | 已修复 |
| RB-08 | 进入正常战斗画面时被识别成战斗失败或错乱页面，可能提前结束或执行错误分支。 | 失败识别条件过宽，普通战斗文案和错乱文案没有严格分层。 | 收窄战斗错乱判断并补充特殊页面测试。 | 已修复 |
| RB-09 | 卡牌复制没有优先选择配置的目标主战员。 | 复制页只按默认点击顺序处理，没有使用目标头像候选。 | CopyMemberPrioritizesTargetMemberAvatar 覆盖头像优先，未命中才回退默认顺序。 | 已修复 |
| RB-10 | 卡牌奖励页选择了不在配置范围内、本应跳过的卡牌。 | 卡牌优先级匹配和“无匹配”分支不完整。 | CardRewardSkipsWhenNoConfiguredCardMatches、CardRewardSkipsWhenRecognitionFindsNoCards 覆盖跳过。 | 已修复 |
| RB-11 | 删卡页面已经选中卡牌，但没有继续点击“移除”，流程卡住。 | 删卡拆成了两步，却把选牌结果误当成动作完成。 | 两张真实流程素材分别覆盖“先选牌”和“选中后点移除”，见 ChaosModeTaskScreenshotFrameBusTests.cs。 | 已修复 |
| RB-12 | 装备页点击主战员后没有继续点击“确认/提炼”，装备无法赋予。 | 选人和装备确认被当成两个可独立结束的动作，且成功分支返回值错误。 | EquipmentPageClicksConfirmAfterSelectingMember 覆盖多步流程。 | 已修复 |
| RB-13 | “请选择主战员”的装备页因为存在禁用状态的“确认”文字，被错误路由给确认消费者，持续产生 NoConsumer。 | 用按钮文字存在代替按钮激活状态。 | IsEquipmentPage 同时检查 IsButtonActive；禁用确认仍交给装备消费者。 | 已修复 |
| RB-14 | 实际页面标题为“获得卡牌”时没有消费者，持续产生 NoConsumer。 | 路由只匹配“卡牌奖励”，没有覆盖游戏实际文案。 | 卡牌奖励路由同时识别“卡牌奖励”和“获得卡牌”；无优先级命中时点击 OCR 找到的“关闭”。 | 已修复 |
| RB-15 | 命运页面先执行无意义的中心确认局部 OCR，连续未命中后才退回全图 OCR。 | 页脚“确认”路由仍然武装了下一帧的中心弹窗探测。 | 页脚 confirm 不再武装中心探测；只有中心确认会话可保持该状态。 | 已修复 |
| RB-16 | 进入下一层时冥想卡牌状态和待处理删卡数量被意外清空。 | ResetLayer() 复用了会重置全部状态的 ResetAll()。 | ResetLayer() 明确保留 MeditationCards 和 PendingRemovedCardCount。 | 已修复 |
| RB-17 | 同一主战员可能安装两件传说装备。 | 装备决策漏掉了 Python 已有的其他槽位传说互斥条件。 | EquipmentDecisionRejectsSecondLegendaryForSameMember 覆盖。 | 已修复 |
| RB-18 | 配置了 Boss 前自动暂停，但运行时没有真正暂停。 | 页面判断存在，暂停请求接口没有接入会话。 | 接入真实暂停请求，RouteSelectionPausesBeforeConfiguredBossLayer 覆盖只请求一次。 | 已修复 |
| RB-19 | 开启“首层刷特定闪光”后，配置没有完整作用到奖励结算和退出重刷链路。 | 只迁移了配置和闪光选择，没有迁移后续结算链路。 | 补齐奖励结算、领取和只打第一层分支，并有正反路径测试。 | 已修复 |
| RB-20 | 谈判页可能把“同步失败”“战斗失败”等普通文案误判为需要点击的失败。 | 失败条件使用了过宽的子串匹配。 | 改为精确匹配“失败”，并覆盖无失败文字和其他失败文案。 | 已修复 |
| RB-21 | 装备赋予没有稳定优先给“刷存档主战员”：配置目标但头像未识别时会回退 1 号位；安装页漏掉“请选择主战员”提示或装备类型时，装备消费者等待后还可能被通用确认抢占，出现未选人就确认。 | 目标成员查找使用了第一号位兜底；安装页只靠单个 OCR 提示识别，且选人后没有独立的“等待确认”路由状态。 | 配置目标时禁止回退 1 号位；使用装备词典和单件布局补充识别安装页；新增 EquipmentInstallAwaitingConfirm，选人后由装备消费者等待，确认激活后交给 confirm。EquipmentInstallUsesDictionarySlotAndConfiguredTargetBeforeConfirm、EquipmentInstallWithIncompleteCandidateDoesNotFallBackToConfirm、ConfiguredTargetWithoutAvatarDoesNotRecordFirstMemberEquipment 覆盖。 | 已修复 |
| RB-22 | 事件选中和装备选人后，下一阶段明明已经出现在新 OCR 帧里，却仍被上一动作会话拦截：事件固定等 5 秒重试，装备确认被 equipment 旧键挡住，表现为一次操作需要多次 OCR 和数秒停顿。 | 总线观察使用消费者 ID 而不是页面阶段键；PageActionSession 只按固定时间释放。 | 总线统一使用 ResolveActionBoundaryKey，装备等待确认阶段改用 equipment-confirm；EquipmentConfirmIsNotBlockedByPreviousMemberSelectionAction 覆盖。后续加入的布局指纹重试方案因引发 RB-26 已回退。 | 已修复 |
| RB-23 | 卡牌复制选择主战员时，第一次已经匹配到目标头像并点击，但下一帧尚未出现 copymemberconfirm 就继续点击第二、第三主战员，最终复制到错误成员。 | Python 在每次候选点击后等待约 2 秒确认特征；C# 迁移把等待拆成逐帧处理，却没有保留候选确认窗口，而且目标命中后仍保留默认候选回退。 | 新增 CopyMemberCandidateClickedAt 和 2.5 秒候选确认窗口；目标头像命中后只允许重试同一目标，不再落到其他成员；无目标头像时才按默认顺序回退。进入新的复制卡牌选择页时重置状态。CopyMemberTargetAvatarNeverFallsThroughToOtherMembers、CopyMemberFallsBackToDefaultClickOrderWithoutTargetAvatar 覆盖。 | 已修复 |
| RB-24 | 主战员头像模板已经写入任务方案，但每次运行仍先打开主界面信息统计页；方案设置页看不到已保存头像，改名保存时还可能因为未绑定方案 ID 而丢掉头像关联。 | 运行时只把模板注册给匹配器，没有把“已有基础头像”转换成流程完成状态；头像没有方案级展示模型；保存调用遗漏当前方案 ID。 | 基础头像加载成功后直接设置 SaveTargetMember，跳过主界面确认；新增方案头像目录和五张 48×48 缩略图管理，支持批量导入与删除；一个头像的六个特征整组原子保存；保存固定携带当前方案 ID。CachedTargetMemberAvatarSkipsStatisticsArchiveAtRunStart、MultipleAvatarVariantsRemainAlignedAcrossAllFeatures、DuplicateAvatarDoesNotPartiallyAppendFeatureBundle 覆盖。 | 已修复 |
| RB-25 | 事件中选择战斗后，事件页在切换到战斗预览前短暂残留；程序重复选择战斗，点击“发起攻击”后又返回事件页，最终进入 Y 坐标优先或随机选项分支。 | 动作仲裁只按路由、布局指纹和约 800 ms 重试间隔去重，没有跨页面记录“战斗事件已选、开始战斗已发送”的业务阶段。开始战斗按钮本身已经点击，问题不在按钮匹配缺失。 | 新增 BattleEventFlow，显式记录 Idle、EventSelected、StartAttackRequested；事件残留帧只消费不操作，start_attack_event 只发送一次点击，8 秒超时后受控重试战斗事件，结算或回到路线页时结束流程。BattleEventTransitionDoesNotReselectEventOrRepeatStartAttack、BattleEventFlowTimesOutAndOnlySendsStartAttackOnce 覆盖。详见 12-design-errors-and-guardrails.md 的 ERR-012。 | 已修复 |
| RB-26 | 运行中同一动作被连续发送：休息节点 3 次、事件选项 6 次、谈判跳过 3 次、继续 2 次、休息确认 2 次、商店刷新 2 次。 | RB-22 为解决跨步骤等待，把稳定布局指纹变化和 800 ms 超时放进全局 PageActionSession。OCR 抖动会不断改变指纹，事件消费者又保留“继续重试已选选项”分支，两层共同放大了重复点击。 | 删除 PageActionSession 的时间和 OCR 指纹自动重试，移除“页面动作重试间隔毫秒”配置；同一路由阶段只允许一次动作，显式业务流程继续负责阶段推进。PageActionDoesNotRetryStablePageAfterCapturedTimeouts、PageActionIgnoresOcrLayoutJitterOnTheSameRoute、PageActionGateSuppressesSameRouteUntilRouteChanges 覆盖。 | 已修复 |
| RB-27 | 卡牌复制已经选中正确主战员后，通用确认消费者按 OCR 文本框中心点击；该中心相对 Python 成功点击位置向左漂移约 8 px，落点没有触发底部确认控件，随后原选中状态被取消，页面停在 confirm 路由。 | 把“OCR 识别按钮”和“实际点击命中区域”绑定成同一个文本框中心，OCR 边界抖动会直接改变鼠标落点。 | OCR 仍负责确认按钮存在与激活状态校验，但底部确认控件使用稳定热点 (0.921, 0.931) 点击，并输出 OCR 中心与最终落点；ConfirmUsesStableFooterHotspotWhenOcrBoxShiftsLeft 覆盖左漂场景，禁用灰色按钮仍不点击。 | 已修复 |
| RB-28 | 复制主战员页在确认按钮未激活时还能被 copy-member 处理；按钮激活后，通用 confirm 以更高路由优先级接管当前帧，copy-member 不再收到完整流程，随后点击失败时又被通用页面动作边界永久抑制。 | 把有状态的选人加确认流程错误拆分给两个消费者，并且通用 confirm 只按页脚文字匹配，没有检查页面所有权。 | main-member-flash、copy-member 调整为优先于通用 confirm；通用 confirm 明确排除这两类有状态页面；两个页面消费者各自管理确认冷却重试并调用共享 ClickFooterConfirmAsync。ActiveCopyMemberConfirmationRoutesToCopyMemberConsumer、CopyMemberOwnsActiveFooterConfirmation、CopyMemberConfirmationRetriesOnlyAfterCooldown、MainMemberFlashOwnsActiveFooterConfirmation 覆盖。详见 12-design-errors-and-guardrails.md 的 ERR-013。 | 已修复 |
| RB-29 | 装备候选页点击装备后，页面停在“当前路由动作已经执行”，装备消费者不再继续选人或确认；后续实机 OCR 还把安装页的“请选择主战员”误读成“请选择装备”，使页面切换后仍被当作候选页。 | 修改确认路由时只区分“选人后确认”和“其他 equipment”，没有区分候选选择、安装选人两个前置阶段；安装页判断又只依赖单个提示或词典装备名，候选旧帧与误读帧没有独立等待边界。 | 装备动作按 equipment-choice、equipment-awaiting-install、equipment-install、equipment-confirm 推进；点击候选后等待安装页，旧候选帧不重复点击，并用“装备标题 + 长按已佩戴装备的图标可进行比较”作为安装布局证据。EquipmentChoiceToInstallStageIsNotBlockedByPriorActionBoundary、EquipmentChoiceWaitsForMisreadInstallPageWithoutReclickingCandidate 覆盖候选旧帧、误读安装页、进入选人链路。详见 12-design-errors-and-guardrails.md 的 ERR-014。 | 已修复 |
| RB-30 | Boss 装备选中并点完主战员后仍未点击确认，下一帧被按钮颜色探测误判为灰色，随后改选其他成员并被旧动作边界永久抑制。 | 把 Boss 奖励领取阶段错误延伸成独立的装备分配路径；有状态选人确认又依赖一次像素判色，并把“未判为激活”当成“选人失败”。 | Boss 领取后立即回到普通装备分配逻辑；点击成员时按 Python 2919 行立即记录装备并保存回滚快照；确认阶段以选人后的业务状态和 OCR“确认”为依据，颜色探测只记日志，连续配置轮数失败才回滚重选。EquipmentPageClicksConfirmAfterSelectingMember、AwaitingEquipmentConfirmStillClicksWhenColorProbeLooksDisabled、EquipmentInstallRetriesMemberSelectionAfterConfirmWaitRounds 覆盖。 | 已修复 |
| RB-31 | 事件选项第一次点击后，日志持续显示“当前路由动作已经执行”，但事件消费者不再处理后续 OCR 帧。 | 总线消费者入口在 `ObservePage` 返回 `SuppressDuplicate` 时提前 `return`；`PageActionSession` 只按路由永久占位，没有记录最后动作帧序号，也没有有界重试。 | 总线入口改为只更新确认/阶段状态，不拦截投递；事件路由把“识别失败等待轮数”传给消费者侧动作边界，达到配置帧数后允许重试。PageActionRetriesSameRouteAfterConfiguredFrames、FrameBusRetriesEventOptionAfterConfiguredUnchangedFrames 覆盖。详见 ERR-015。 | 已修复 |
| RB-32 | 卡厄思奖励结算页点击“继续”后约 1.7 秒，在同一语义页面又点击一次“继续”；随后中心确认弹窗出现，但第一次连续点击已经覆盖了正确交互，页面卡住。 | 同一帧先匹配 `chaos-reward-settlement`，处理器返回 false 后又兜底到 `continue` 执行点击。旧仲裁器只保存一个 `_pageAction`，下一帧观察到最高优先级路由时就清除了实际 `continue` 动作保护；`CapturedAt` 只能拒绝点击前旧帧，无法识别点击后仍显示旧页面的新帧。 | `ActionArbiter` 改为按实际动作键独立保存会话；观察其他未执行路由不再释放保护，只有其他路由真正发送动作或中心确认完成后才清理。PageActionSurvivesDifferentRouteUntilThatRouteDispatches、ChaosRewardSettlementDoesNotRepeatFallbackContinueOnUnchangedFrame 覆盖。详见 ERR-016。 | 已修复 |
| RB-33 | 休息或带商店标志的休息页点击“跳过”后，OCR 文本轻微抖动仍会再次点击；通用复制按钮也缺少页面未离开时的动作边界。 | `ConfirmationSession` 只按 OCR 指纹判断来源页面是否离开，把同一语义页面的新帧误当成新页面阶段；`copy` 路由又关闭了默认动作边界。 | `PageObservation` 保存完整候选路由，确认会话按“当前主路由是否仍属于来源候选集合”维持保护；普通路径与帧总线路径同步写入路由身份；`copy` 恢复动作边界。ConfirmationKeepsSourceBlockedWhileSemanticRouteRemains、SkipDoesNotRepeatAcrossOcrJitterOnSameSemanticPage、CopyClicksActiveButtonOnlyOnceUntilRouteChanges 覆盖。详见 ERR-017。 | 已修复 |
| RB-34 | 通用 `confirm` 第一次点击后页面仍为 `confirm`，后续约 89 帧只记录 `int.MaxValue` 抑制，任务不再动作；`close-page` 则在提示不消失时缺少动作会话，可能逐帧重复点击。 | `confirm` 路由没有配置有界重试，沿用 `TryBeginPageAction` 的 `int.MaxValue` 默认值；特殊消费者没有像普通路由一样调用 `TryBeginPageAction` 和 `MarkPageActionSent`，动作边界只被观察、没有被建立。 | `PageActionSession` 增加按新帧和配置轮数计算的有界重试，最多 3 次；`confirm` 与正式 `close-page` 路由统一使用该边界，覆盖层通过 `IsPriorityOverlay` 优先于主战员采集。ClickScreenConsumerRetriesOnConfiguredFreshFramesUpToLimit、FooterConfirmRetriesOnConfiguredFreshFramesUpToLimit、PageActionRetriesSameRouteUpToAttemptLimitAndThenSuppresses、ClosePageOverlayHasPriorityOverTargetMemberArchive 覆盖。详见 ERR-018。 | 已修复 |
| RB-35 | Boss 装备奖励领取后经装备页中转，再次出现 `boss-equipment-reward` 时持续记录 `2147483647`，消费者不再动作；加入有界重试后又出现同一领取按钮连续点击三次。 | 动作会话只在“当前路由启用动作边界”时观察；`equipment` 路由关闭动作边界，中转时没有观察到 Boss 来源路由已经消失，旧会话因此跨页残留。把游戏处理中的同页帧误当成点击失败并启用自动重试，又产生了重复领取。 | `PageActionSession` 保存来源路由；所有已识别语义页都参与观察，并在来源路由不再属于候选路由时释放旧会话；`boss-equipment-reward` 每次语义页面只点击一次，不启用自动重试。`PageActionIsReleasedAfterSourceRouteLeavesObservedCandidates`、`BossEquipmentRewardClicksOnlyOnceUntilRouteChanges` 覆盖。详见 ERR-019。 | 已修复 |
| RB-36 | 中心确认按钮第一次点击后约 2.4 秒，同一坐标又被点击一次，日志表现为跳过按钮和中心确认弹窗各点击一次。 | `ConfirmationSession` 只在 OCR 指纹完全相同时把弹窗视为仍存在；点击后的动画或 OCR 抖动改变指纹后，连续两帧被误算成“来源页面已离开”，旧会话被重置，下一帧又创建新的确认意图。另有“确认弹窗仍存在时按 6 帧自动重试”的独立重复点击风险。 | 确认命令发出后，只要中心按钮位置或语义路由仍指向同一弹窗，就保持会话并禁止重复点击；删除中心弹窗的固定帧自动重试。只有弹窗消失或语义页面真正切换后才结束会话。`ConfirmedDialogWithOcrJitterDoesNotIssueRepeatedConfirm` 覆盖 9 帧指纹抖动且不产生第二次确认。详见 ERR-020。 | 已修复 |
| RB-37 | 事件选项在同一页面被连续点击 3 次；后续第一次改成“永不补点”后，首次点击未生效时任务会永久停在事件页。装备安装确认在约 2.9 秒后也曾被再次点击。 | `event-task` 最初让路由级重试和消费者内 `PendingEventOptionAttempts` 两层各自累计次数，导致最多 3 次点击；过度修复又同时删除两层，使失败点击没有任何恢复路径。装备安装确认则把“确认按钮仍被 OCR 到”误判为“第一次点击未生效”。 | 事件路由按 `识别失败等待轮数` 在新鲜帧上重新开放一次判断，消费者把实际点击严格限制为首次点击加 1 次补点；达到 2 次后只等待页面变化。装备安装确认发出后同一安装页只允许一次点击。新增 `FrameBusRetriesEventOptionOnceWhenUnchangedPagePersists`、`EquipmentInstallConfirmationIsClickedOnlyOnceWhilePageRemains`，并保留原动作边界测试。详见 ERR-021。 | 已修复 |

## 3. 帧总线、并发和生命周期缺陷

| ID | 修改引入的缺陷 | 根因 | 当前修复与回归保护 | 状态 |
|---|---|---|---|---|
| FB-01 | 截图、OCR、路由和消费者动作串在同一条等待链上；消费者等待时，日志停止、画面继续更新但不再判断。 | 采集循环直接等待业务动作完成。 | 改为非阻塞帧投递；RunOncePublishesFrameWithoutWaitingForConsumerClick 覆盖。 | 已修复 |
| FB-02 | 相同 ConsumerId + TextSignature 被总线直接抑制，页面未变化时后续帧无法再确认或重试。 | 去重职责放错到总线。 | 总线每帧交付，消费者自行决定等待、重试或丢弃。 | 已修复 |
| FB-03 | ActionArbiter 放在消费者业务入口之前，旧动作锁可以永久拦住真正处理器，表现为总线“不再派发”。 | 总线与业务状态机耦合，消费者入口反向锁死投递。 | 总线只负责选择消费者；消费者内部决定业务生命周期。 | 已修复 |
| FB-04 | 单个消费者卡住后，后续帧不断堆积或丢失，任务无法自愈。 | 没有消费者看门狗和 worker 替换机制。 | 增加消费者超时、故障 worker 隔离和下一帧重建；TimedOutConsumerIsReplacedForLaterFrames 覆盖。 | 已修复 |
| FB-05 | 超时或停止时，已排队 Payload 没有可靠释放，可能持有帧租约和图像资源。 | 只处理当前动作，没有枚举并清理消费者队列。 | 停止和超时路径统一释放未消费 Payload，TimedOutConsumerDisposesQueuedPayloads 覆盖。 | 已修复 |
| FB-06 | _lastSequence 被采集线程和消费者线程并发写入，可能发生丢失更新和帧序号回退。 | 普通读写没有原子性和单调保证。 | 使用 Volatile.Read 和 Interlocked.CompareExchange 单调推进。 | 已修复 |
| FB-07 | 取消后消费者仍可能从队列再取一帧并执行动作，停止按钮之后存在“最后一个误点击”风险。 | 取消只终止等待，没有在每帧执行前后阻止新业务动作。 | 执行前后检查取消；剩余队列由释放逻辑清理，并断言取消后消费者调用次数仍为 1。 | 已修复 |
| FB-08 | 无消费者、消费者拒绝、队列溢出和消费者失败没有统一错误类型，排查只能靠猜测，部分错误日志还被归为 Debug 或噪声。 | 投递失败没有结构化结果和明确严重级别。 | 增加 FrameDispatchErrorKind、结构化错误和级别测试；NoConsumer、ConsumerRejected 等可区分。 | 已修复 |
| FB-09 | 消费者超时或单个消费者异常时，取消和恢复范围不清，可能影响后续帧或其他消费者。 | worker 生命周期与总线生命周期边界不明确。 | 每个消费者使用独立 worker 生命周期；故障 worker 可被替换，其他消费者继续工作。 | 已修复 |

> **2026-10-17 纠正 RB-26：** “同一路由永不重试”只能用于局部动作边界的即时去重，不能成为总线消费者入口的永久锁。事件路由现在按 `识别失败等待轮数` 进行有界重试；总线仍要每帧投递。
## 4. OCR、输入、性能和开发样本缺陷

| ID | 修改引入的缺陷 | 根因 | 当前修复与回归保护 | 状态 |
|---|---|---|---|---|
| VI-01 | OCR 未识别到按钮时直接点击固定坐标，可能误点并制造循环。 | 固定坐标被当成正常兜底，而不是识别失败信号。 | 固定坐标兜底被禁止；按动作独立等待轮数，达到阈值保存证据并停止猜点击。 | 已修复 |
| VI-02 | 曾把单元测试合成的“跳过”文本框当成真实截图证据，同时卡牌类型 OCR 的硬条件导致真实卡牌识别为空。 | 测试夹具与真实识别证据混淆，卡牌识别条件过严。 | 真实素材走真实 OCR/特征匹配；普通闪光允许高置信度类型图标在类型 OCR 缺失时通过，但仍要求名称和描述。 | 已修复 |
| VI-03 | 人工增加了“连续三帧才处理”的规则，导致已经完成 OCR 的页面仍不消费，表现像总线停止。 | 自行增加不存在的业务需求，破坏了每帧识别原则。 | 删除三帧门控；OcrRunsOnEveryFrameEvenWhenNoHandlerMatches 和 UnhandledPageLogsOnFirstFrameWithoutThreeFrameGate 覆盖。 | 已修复 |
| VI-04 | 普通点击被实现成长按或悬停，游戏没有该交互需求，流程会卡在按下状态。 | 输入命令没有保持“普通点击”边界，擅自扩大交互语义。 | 普通流程只使用点击；长按仅限明确存在的面具/刻印类需求，未迁移功能不得借用普通点击入口。 | 已修复 |
| VI-05 | 每次动作使用固定等待并反复全图 OCR，一次点击可能耗时数秒，停止响应也变慢。 | 用睡眠代替轮询新帧，热路径没有区域识别。 | 去掉固定睡眠，按新帧持续识别；增加中心确认区域探测，未命中达到配置轮数后全图兜底。 | 部分完成，ROI 范围继续按页面扩展 |
| VI-06 | 开发样本最初带有额外标注，且没有覆盖所有实际消费者和 NoConsumer 帧，无法作为稳定回归素材。 | 归档挂在页面判断而非总线下发，样本内容与消费边界不一致。 | 改为保存干净 PNG 和带坐标、置信度的 OCR JSON，按 ConsumerId 分类，NoConsumer 使用独立类别。 | 已修复 |
| VI-07 | 每个消费者每轮只保存一张，重复帧也会占用名额，不符合“每个消费者保留 20 张不重复截图”的要求。 | 归档按运行批次限流，没有按图片哈希去重和累计上限。 | 改为跨运行保留、哈希去重、每消费者最多 20 张；覆盖重复、上限和临时文件成对写入测试。 | 已修复 |
| VI-08 | 样本归档 UI 仍显示“每次运行每个消费者只保留 1 张”，与实际的“累计 20 张不重复截图”不一致。 | 行为已修改，但初始文案和状态文案没有同步清理。 | 记录为待同步缺陷：Kes.Gui/MainWindow.xaml 和 MainWindowViewModel.cs 的初始文案仍含旧描述。 | 待同步 |
| VI-09 | 普通战斗、路线、奖励、事件等页面持续先做中心确认局部 OCR，连续 miss 后才回退全图 OCR，单次操作常额外增加约 1.3 秒。 | 探测状态按“当前路由不是确认消费者”广泛预置，把可能出现中心确认误当成普通页面的默认下一帧路径。 | 仅由明确动作或路由元数据武装探测；普通关闭与页脚确认不再预置。OrdinaryCloseActionDoesNotArmCenterProbeForFollowingContinuePage、FooterConfirmDoesNotArmCenterProbeForFollowingDestinyPage 及 ROI 命中/回退用例覆盖。详见 ERR-010。 | 已修复 |
| VI-10 | 卡牌复制进入“请选择要复制的卡牌”页面后被通用 `skip` 消费者接管，表现为复制被取消。 | Python 的 `handle_copy_card_choice` 尚未迁移，页面专用标题没有对应路由，通用跳过成为实际消费者。 | 新增 `copy-card-choice` 专用路由与处理器，优先级先于 `copy-member` 和 `skip`；覆盖 target 优先、配置优先级及总线路由回归。详见 ERR-011。 | 已修复 |

## 5. GUI 和配置回归

| ID | 修改引入的缺陷 | 根因 | 当前修复与回归保护 | 状态 |
|---|---|---|---|---|
| UI-01 | Windows PC 目标仍显示模拟器专用的捕获方式和交互方式。 | 运行目标与 UI/输入能力没有按目标类型过滤。 | PC 目标只展示 PC 可用能力；模拟器能力延迟到目标实现后显示。 | 已修复 |
| UI-02 | 捕获方式和交互方式移入设置后，主页布局错位、控件重叠。 | 调整信息层级后没有重新约束网格和固定尺寸。 | 重新规划主页区域并恢复“运行目标”原始尺寸。 | 已修复 |
| UI-03 | 项目内 Lucide 图标不显示，任务开始、暂停、停止等按钮难以辨认。 | 图标包资源或 XAML 命名空间/资源引用不完整。 | 统一接入 Lucide 图标资源和按钮样式。 | 已修复 |
| UI-04 | 任务实际运行，但 UI 状态显示为“已停止”。 | 会话状态绑定和任务运行状态不是同一数据源。 | 状态刷新与会话生命周期同步。 | 已修复 |
| UI-05 | 主战员、卡牌列表等长 OCR 文本在单行输入框中显示不全。 | 把列表型长文本当作普通单行配置。 | 增加独立列表编辑器，每项独占一行并显示完整 OCR 文本。 | 已修复 |
| UI-06 | 拖动优先级没有明确落点，还显示等号，用户无法判断松手后的位置。 | 拖拽视觉反馈不足，使用了不直观的装饰。 | 增加动画插入线和目标位置反馈，移除等号。 | 已修复 |
| UI-07 | 开发测试截图被缩放，无法按 1600x900 游戏原始尺寸查看和标注。 | 预览区域直接受主窗口尺寸约束。 | 增加 1:1 独立预览，并支持区域框选、裁剪、OCR 和模板调试。 | 已修复 |
| UI-08 | 未启用赛季时装备配置消失；启用刻印赛季后又混入默认装备和面具选项。 | 赛季、装备、刻印和面具被当成了同一个总开关。 | 按赛季下拉条件拆分页面区块；装备与刻印开关独立，不属于当前赛季的功能隐藏。 | 已修复 |

## 6. 为什么会重复出现同类 BUG

### 6.1 只修表面页面

休息页卡住时曾经只修休息页，卡牌页卡住时又修卡牌页。真正的问题是共享确认协议、动作所有权和页面离开确认缺失。判断同类问题时必须问：

~~~text
这个页面是否也会出现“点击 -> 确认 -> 原页面仍在”？
这个动作是否也可能由旧帧、重复帧或消费者重试触发？
这个按钮文字在禁用状态下是否仍然存在？
~~~

### 6.2 把“看到了文字”当成“可以执行”

装备页的“确认”文字在未选择主战员时仍然存在，但按钮不可执行。以后所有按钮识别必须同时具备：

~~~text
页面正确
+ 文本或特征正确
+ 按钮激活或业务前置条件满足
+ 帧晚于上一次输入完成时间
~~~

### 6.3 把总线当成业务状态仓库

总线不得保存“这个页面已经点过”“这个消费者还没返回”这类业务判断。总线每次看到新帧都要完成路由和投递；是否再次点击、等待多久、是否拒绝，由消费者自己负责。

### 6.4 用固定值代替状态机

固定坐标、固定睡眠、固定帧数和永不过期的动作锁都只能短期让流程看起来前进。正确替代分别是：

| 错误做法 | 正确做法 |
|---|---|
| OCR 失败后点固定坐标 | 等待新帧重新识别；达到轮数保存现场并停止猜测 |
| 点击后等待固定秒数 | 丢弃旧帧，持续轮询新帧并观察页面变化 |
| 连续三帧相同才处理 | 每帧都 OCR 和投递，消费者按自己的状态决定 |
| 普通点击用长按 | 只实现明确要求的输入语义 |
| 总线按相同文字去重 | 消费者按业务流程决定是否重试或等待 |

### 6.5 缺少反路径测试

很多 BUG 的正向用例已经通过，但反路径没有覆盖：

- 按钮存在但禁用。
- 页面标题有同义词或 OCR 轻微差异。
- 页面点击后短暂不变化。
- 输入之后又返回一张旧截图。
- 取消发生在消费者队列中还有帧的时候。
- 相同页面连续出现，但消费者状态已经变化。
- 一个消费者失败时，其他消费者仍应继续。

## 7. 强制防复发检查表

每次修改自动化流程前必须逐项回答：

1. 这是页面数据、消费者状态、动作生命周期还是总线职责？
2. 是否新增了页面专用布尔值来代替共享确认或动作对象？
3. 是否用文字存在代替按钮激活和业务前置条件？
4. 是否新增固定坐标、固定睡眠、固定帧数或不受控重试？
5. 输入完成后是否会拒绝点击前截取的旧帧？
6. 页面未变化时，下一帧是否仍会 OCR、路由和投递？
7. 消费者拒绝时，是否记录了帧号、消费者、OCR 摘要和实际路由？
8. 取消、超时和队列释放时，是否保证不再执行新的业务动作？
9. 同类页面是否全部检查，而不是只修用户指出的一个页面？
10. 是否同时覆盖命中、未命中、禁用、旧帧、重复帧和恢复路径？
11. 修改行为后是否同步更新 UI 文案、设计文档和迁移台账？
12. 是否引入了用户没有要求、Python 基线也没有的交互或等待规则？

## 8. 不计入修改 BUG 的项目

以下事项容易与缺陷混淆，但目前不应计入本记录：

- 商店删牌上限统计：复核后确认 C# 与 Python 都按商店删牌数限制，原审查结论是误判。
- 路线优先级中的“商店”：复核后确认列表有商店时按排名、无商店时固定优先，原审查结论是误判。
- 刻印、面具和赛季专属功能尚未迁移：属于明确的范围缩减和待办，不是本次修改引入的运行时 BUG。
- `handle_copy_card_choice` 曾因未迁移而被通用 `skip` 路由接管，属于迁移缺口而不是本次修改引入的 BUG；现已补齐专用消费者，记录见 10-chaos-mode-migration-ledger.md 第 67 项和 12-design-errors-and-guardrails.md 的 ERR-011。
- OCR 模型本身的识别率波动：如果没有可复现的代码路径错误，不能直接归因于本次修改；应先保存干净样本和 JSON，再用真实 OCR 测试确认。

## 9. 相关文档与测试入口

- 设计错误详解：12-design-errors-and-guardrails.md
- 迁移和总线审查：13-migration-review-findings.md
- 区域切片与总线方案：11-region-slices-and-event-bus.md
- 总线单元测试：Automation.Core.Tests/FrameDispatchBusTests.cs
- 路由与消费者回归：Automation.Core.Tests/ChaosModeTaskFrameBusTests.cs
- 真实流程截图素材测试：Automation.Core.Tests/ChaosModeTaskScreenshotFrameBusTests.cs
- 帧新鲜度测试：Automation.Core.Tests/FrameFreshnessTests.cs
- 样本归档测试：Automation.Core.Tests/FrameSampleArchiveTests.cs

## 10. 当前验证基线

截至 2026-10-17：

- 全量自动化测试：281/281 通过。
- 最新补充回归：语义路由不变时确认会话持续保护；OCR 抖动不会再触发重复跳过，通用复制按钮也恢复页面动作边界。
- 静态分析构建：0 警告、0 错误。
- 最新补充回归：结算页兜底“继续”只发送一次；时间戳只负责拒绝点击前旧帧，页面动作保护按实际动作键独立维护。

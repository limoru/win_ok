# ChaosMode 行为等价迁移账本

本表按 Python `ok_tasks/utils_chaos.py` 中 `PAGE_HANDLERS` 的原始顺序维护。迁移阶段以行为等价为第一目标。当前全部页面入口已完成唯一帧路由接入，后续行为迁移继续按本表推进。

状态说明：

- `已完成`：已有 C# 方法、已接入处理器顺序，并有对应纯逻辑测试或明确的组合测试。
- `正常流程实现`：已覆盖当前实机验证所需的普通流程；赛季、刻印、面具或复杂分支暂缓，并在备注中列明。
- `待迁移`：Python 入口已确认，C# 尚未实现。
- `待核对`：已有实现，但还需要补分支测试或确认与 Python 的行为差异。
- `有意差异`：C# 不复制 Python 的明显缺陷，已在代码和测试中记录。

测试规则：

- 每个 handler 至少覆盖“命中”和“未命中”两条路径。
- 涉及状态修改、配置读取、点击坐标或分支判断时，需要单独断言。
- 不使用真实游戏画面和真实输入验证 handler。

## 入口清单

| # | Python handler | C# 方法 | 状态 | 备注 |
|---:|---|---|---|---|
| 1 | `handle_auto_stop` | `HandleAutoStopAsync` | 待迁移 | 依赖 `几轮后停止(0为不停止)` 和 `total_rounds`。 |
| 2 | `handle_save_target_member` | `HandleSaveTargetMemberAsync` | 正常流程实现 | 已接入主界面血量识别、进入信息统计页和单局存档主战员特征准备。 |
| 3 | `handle_route_selection` | `HandleRouteSelectionActionAsync` | 正常流程实现 | 已接入节点识别、小地图规划、可见节点兜底和路线优先级；刻印专业模式与 Boss 前运行时暂停暂缓。 |
| 4 | `log_credit` | `LogCredit` | 待迁移 | 仅记录信用点，不拦截后续 handler。 |
| 5 | `log_node_status` | `LogNodeStatus` | 待迁移 | 需要统一的进度状态输出。 |
| 6 | `handle_stuck_log` | `HandleStuckLogAsync` | 待迁移 | 依赖帧变化检测和多种兜底 handler。 |
| 7 | `handle_close_page` | `HandleClosePageAsync` | 已完成 | |
| 8 | `handle_refine_equipment_credit` | `HandleRefineEquipmentCreditAsync` | 已完成 | |
| 9 | `handle_center_confirm` | `HandleCenterConfirmAsync` | 已完成 | |
| 10 | `handle_archive_target_member` | `HandleArchiveTargetMemberAsync` | 正常流程实现 | 每次仍进入信息统计页校验主战员槽位；先以任务方案中的多个 `target_member` 头像候选匹配，命中后直接退出，未命中才按 OCR 名称查找并追加新头像模板。复制、选卡、装备、面具和大图整组头像特征都随任务方案持久化，每个特征最多保留 5 个候选，可直接随方案导出复用。 |
| 11 | `handle_chaos_mask_engraving` | `HandleChaosMaskEngravingAsync` | 待迁移 | 依赖面具卡牌刻印配置和 OCR。 |
| 12 | `handle_equipment` | `HandleEquipmentAsync` | 正常流程实现 | 已接入普通装备价格、词典槽位、目标主战员、优先级和品质比较；刷装备刻印与刻印优先级暂缓。 |
| 13 | `handle_card_assign` | `HandleCardAssignAsync` | 正常流程实现 | 已接入普通卡牌奖励分配、购买页取消/购买和目标主战员选择；滚动、复杂刷新和赛季分支暂缓。 |
| 14 | `handle_confirm` | `HandleConfirmAsync` | 已完成 | |
| 15 | `handle_mask_card` | `HandleMaskCardAsync` | 待迁移 | 依赖指定面具和刷新次数。 |
| 16 | `handle_convert` | `HandleConvertAsync` | 已完成 | |
| 17 | `handle_shop` | `HandleShopAsync` | 正常流程实现 | 已接入普通商品选择、免费刷新、删牌入口和 3 张中立牌限制；付费刷新、刻印角标与专业模式暂缓。 |
| 18 | `handle_rest` | `HandleRestAsync` | 正常流程实现 | 已接入普通休息、冥想条件判断、血量取舍和德朗商店入口。 |
| 19 | `handle_close_button` | `HandleCloseButtonAsync` | 已完成 | |
| 20 | `handle_remove` | `HandleRemoveAsync` | 正常流程实现 | 已接入移除按钮状态判断，并区分商店删牌与本局总删牌计数。 |
| 21 | `handle_three_choice_card_remove` | `HandleThreeChoiceCardRemoveAsync` | 待迁移 | 依赖三选一删牌逻辑。 |
| 22 | `handle_flash` | `HandleFlashAsync` | 有意差异 | C# 改为精确匹配，避免遮住后两个闪光 handler。 |
| 23 | `handle_reflash` | `HandleReflashAsync` | 已完成 | |
| 24 | `handle_grant_flash` | `HandleGrantFlashAsync` | 已完成 | |
| 25 | `handle_copy` | `HandleCopyAsync` | 已完成 | |
| 26 | `handle_leave` | `HandleLeaveAsync` | 已完成 | |
| 27 | `handle_mental_breakdown` | `HandleMentalBreakdownAsync` | 已完成 | |
| 28 | `handle_data_collected` | `HandleDataCollectedAsync` | 待迁移 | 依赖存档价值阈值和删除特征。 |
| 29 | `handle_expedition_result` | `HandleExpeditionResultAsync` | 已完成 | `engraving_log` 仍为 TODO。 |
| 30 | `handle_next_step` | `HandleNextStepAsync` | 已完成 | |
| 31 | `handle_craft` | `HandleCraftAsync` | 已完成 | |
| 32 | `handle_select` | `HandleSelectAsync` | 已完成 | |
| 33 | `handle_go_to_chaos_core` | `HandleGoToChaosCoreAsync` | 已完成 | |
| 34 | `handle_equipment_recast` | `HandleEquipmentRecastAsync` | 已完成 | |
| 35 | `handle_minimizemap` | `HandleMinimizemapAsync` | 已完成 | 已覆盖命中与未命中路径，使用 IFeatureMatcher。 |
| 36 | `handle_weakness_info` | `HandleWeaknessInfoAsync` | 已完成 | |
| 37 | `handle_non_battle_page` | `HandleNonBattlePageAsync` | 已完成 | 已覆盖故事、营救、方舟城市停止请求和普通页面不误触。 |
| 38 | `handle_battle_crash` | `HandleBattleCrashAsync` | 已完成 | 已覆盖三种错乱文案与未命中路径。 |
| 39 | `handle_battle_auto_check` | `HandleBattleAutoCheckAsync` | 已完成 | 已覆盖手牌计数、start_attack_event 点击、自动战斗白色占比和最终 Boss 标记。 |
| 40 | `handle_settlement` | `HandleSettlementAsync` | 已完成 | |
| 41 | `handle_destiny_choice` | `HandleDestinyChoiceAsync` | 正常流程实现 | 已接入命运标题识别和随机选择；确认继续交给后续处理器。 |
| 42 | `handle_main_member_flash` | `HandleMainMemberFlashAsync` | 待迁移 | 依赖主战员闪光特征。 |
| 43 | `handle_card_reward` | `HandleCardRewardAsync` | 正常流程实现 | 已接入优先级选牌、刷新和跳过；target、刷初始卡牌和赛季分支暂缓。 |
| 44 | `handle_select_card` | `HandleSelectCardAsync` | 正常流程实现 | 已接入当前可见卡组的优先级、咒术/基础牌兜底和跳过/取消；滚动浏览与刷空档同排选择暂缓。 |
| 45 | `handle_copy_member` | `HandleCopyMemberAsync` | 正常流程实现 | 已按 `target_member_large` 头像优先点击目标主战员，未命中时按原顺序回退，并等待 `copymemberconfirm`。 |
| 46 | `handle_convert_card` | `HandleConvertCardAsync` | 已完成 | |
| 47 | `handle_discovery_select` | `HandleDiscoverySelectAsync` | 已完成 | 已覆盖配置阈值选择、无合格项取消和存储数据解析。 |
| 48 | `handle_negotiation` | `HandleNegotiationAsync` | 有意差异 | 修正 Python 反向 `in` 判断，按意图判断包含“失败”。 |
| 49 | `handle_chaos_reward_settlement` | `HandleChaosRewardSettlementAsync` | 待迁移 | 依赖奖励领取配置修改。 |
| 50 | `handle_chaos_reward_claim` | `HandleChaosRewardClaimAsync` | 待迁移 | 依赖战利品验证卡和配置修改。 |
| 51 | `handle_continue` | `HandleContinueAsync` | 已完成 | |
| 52 | `handle_season_chaos_initial_page` | `HandleSeasonChaosInitialPageAsync` | 待迁移 | 依赖存储数据阈值。 |
| 53 | `handle_zero_system_initial_page` | `HandleZeroSystemInitialPageAsync` | 待迁移 | 依赖存储数据阈值。 |
| 54 | `handle_disable_adaptation_distortion` | `HandleDisableAdaptationDistortionAsync` | 待迁移 | 依赖刷空档配置和像素颜色。 |
| 55 | `handle_enter` | `HandleEnterAsync` | 已完成 | |
| 56 | `handle_obtain_reward` | `HandleObtainRewardAsync` | 已完成 | |
| 57 | `handle_view_original` | `HandleFlashEffectAsync` | 正常流程实现 | Python 函数名沿用了“查看原件”，实际业务是“请决定闪光效果”页面；已接入普通卡牌、target 卡牌、闪光优先级和冥想状态标记，赛季卡牌和滚动分支暂缓。 |
| 58 | `handle_trauma_center` | `HandleTraumaCenterAsync` | 待迁移 | 依赖旅行券识别和治疗配置。 |
| 59 | `handle_treating` | `HandleTreatingAsync` | 已完成 | |
| 60 | `handle_treat_approve` | `HandleTreatApproveAsync` | 已完成 | |
| 61 | `handle_zero_system_home` | `HandleZeroSystemHomeAsync` | 已完成 | |
| 62 | `handle_codex_search` | `HandleCodexSearchAsync` | 已完成 | |
| 63 | `handle_expedition_unlock` | `HandleExpeditionUnlockAsync` | 已完成 | |
| 64 | `handle_memory_elimination` | `HandleMemoryEliminationAsync` | 已完成 | |
| 65 | `handle_chaos_craft` | `HandleChaosCraftAsync` | 已完成 | |
| 66 | `handle_conquer_difficulty` | `HandleConquerDifficultyAsync` | 已完成 | |
| 67 | `handle_copy_card_choice` | `HandleCopyCardChoiceAsync` | 待迁移 | 依赖卡牌复制优先级。 |
| 68 | `handle_skip` | `HandleSkipAsync` | 已完成 | |
| 69 | `handle_event_task` | `HandleEventTaskAsync` | 正常流程实现 | 已接入事件选项识别、禁用项过滤、check、必选/拉黑/任务优先级、战斗事件和随机兜底；宝库与刷初始分支暂缓。 |
| 70 | `handle_held_cards_page` | `HandleHeldCardsPageAsync` | 已完成 | |
| 71 | `handle_escape` | `HandleEscapeAsync` | 已完成 | |
| 72 | `handle_stage_end_data_details` | `HandleStageEndDataDetailsAsync` | 已完成 | |

## 当前统计

- 总入口：72
- 已完成：43
- 正常流程实现：10
- 有意差异：2
- 待迁移：18

## 迁移阶段约束

- 先完成底层接缝，再实现依赖这些接缝的 handler。
- handler 实现与测试放在同一批中，不把测试集中留到最后。
- Python 的明显缺陷只有在记录为“有意差异”后才允许改变行为。
- `ChaosMode` 已开放为 `Available` 供实机验证正常流程；赛季、刻印、面具和其余待迁移入口未完成前，不得把模块描述改成“完整支持”。
# HMapSync-CN 移植说明

基于 [Enceladeum/HMapSync](https://github.com/Enceladeum/HMapSync) 1.1.0.5（2026-11-20 浅克隆），适配国服客户端 2026.11.03。**开发中。** 上游许可证为 AGPL-3.0，本目录的修改同样以 AGPL-3.0 发布；分发构建结果时需要一并提供源码。

## 构建

- 保留 `Dalamud.NET.Sdk/15.0.0`，`Directory.Build.props` 把 `DALAMUD_HOME` 指向 `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`，引用国服 Dalamud。
- `dotnet build -c Release`，输出 `bin\Release\HMSync.dll`。
- 显示名是 HMapSync-CN，InternalName 与原版相同（`HMSync`），只能装一个；重载插件前先 `/hms stop`。

## 依赖

- MessagePack 固定为 2.5.303（修复 GHSA-qhrr-8q5h-9q3h）。该漏洞需要反序列化到 `object`；本插件现有 18 个协议类型未发现此类公开成员。
- 1.1.1.0（2026-11-22）起随包 2.5.303。

## 国服适配

- `QuestSpoofService.SigGetQuestSequence` 改为 `E8 ?? ?? ?? ?? 3A 43 06`（原特征码在国服第一处匹配到错误函数）。其余特征码已在国服 exe 中扫过。
- 地图分类改按 ContentType RowId 判断，房区名改从 PlaceName 读取（原代码按英文名，国服失效）。
- `HMSyncUI.cs` 界面与命令说明汉化；分类键保持英文，显示走 `CatLabel()`。场景名和任务名走 `SceneNames`；聊天提示（`chat.Print`）未翻译。
- `/hms load <id>` 支持直接输入地图编号。同名地图（如诗想空间 968 / 1166）在列表中显示“副本名 · 编号”。

## 新增功能（“地图控制”页 → 场地机关与区域）

| 功能 | 实现位置 | 说明 |
| --- | --- | --- |
| 场地机关 | `Services/MapEffectService.cs`、`ZoneLoadService.SetupInstanceContentForZone` | 只对 ContentLinkType==1、战斗类 IntendedUse 且有 ContentDirectorManagedSG 的副本创建 director；加载约 2 秒后自动套开场状态 |
| 阶段预设 | `MapEffectService.FlowPresets` / `SgbFlowPresets`、`HMSyncConfig.MapPhasePresets` | 内置：光暗未来、欧米茄、妖星乱舞、巴哈、神兵、龙诗、亚历山大、M11/M11S；可自存机关 + 天气 + 节日 + 隐藏图层 |
| 区域跳转 | `MapEffectService.GetZoneAreas` | 读 planmap/planevent 的 `LVD_*` 图层（深层迷宫终点、Boss 区等） |
| 环境天气 | `MapEffectService.GetZoneWeathers` | 解析 `.lvb` → `.envb` 的 ENVS 表，原生写入 |
| 剧情进度 / 布局版本 | `MapEffectService.GetLayoutKeys`、插件 `LoadWithLayoutKey` | 原始解析 LGB 图层集键；同图时先经旅馆 177 再冷加载；名称按图层名推断（阶段 vN） |
| 节日装饰 | `MapEffectService.Get/SetFestivals`、`Services/FestivalNames.cs` | 名称表由 Hyperborea festivals.yaml 生成 |
| 场景图层 | `MapEffectService.GetSceneLayers`、`ZoneLoadService.ApplyIdSet` | 显示路径补上 SharedGroup 子物体恢复；换图时释放本插件持有的隐藏 |
| 穿墙速度 | `NoclipService.SpeedMultiplier` | 1–6 倍；仅在已加载虚拟地图且封包拦截开启时生效。飞行、穿墙、地毯本身是上游功能 |
| 中文场景名 | `scene-names.zh.json`、`Services/SceneNames.cs` | 209 个场景名、719 个任务名。两服客户端按 RowId 对照；场景名里 112 个是官方译名，69 个按官方术语手译，28 个由前两者派生。搜索中英文都可用，同步协议仍传英文名 |
| 切歌延迟 | `MapSettingsService.PlayBgm` | 切歌前设自定义淡入淡出 100ms |

## 来源

- 第三方许可全文见 `THIRD_PARTY_NOTICES.md`。
- 机关槽位 / SGB 状态名解析、节日、剧情进度思路：[Hyperborea](https://github.com/kawaii/Hyperborea)（BSD-3-Clause，© kawaii & NightmareXIV），源文件头已保留声明。
- 绝本各阶段的天气与机关状态：[AnoMech](https://github.com/anomek/AnoMech) 的实战回放数据。

## 实测状态（2026-11-20）

- 已验证：国服加载地图；光暗未来通关花海；至福乐土（1073）布局版本切换。
- 2026-11-22 离线核对：心跳、发包、交互、飞行四个特征码在国服 exe 中均唯一；/say 放行默认关闭，未在国服重新学习前不会放行该包。
- 未验证：中文场景名显示、穿墙调速、布局切换的中途取消、欧米茄、妖星乱舞、M11S 流程，亚历山大分阶段图层隐藏，龙诗阶段天气，切歌淡入淡出，换图释放隐藏记录。
- 多人同步：官方中继需要封测密钥，计划自建中继，未实现。

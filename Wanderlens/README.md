# Wanderlens 漫游镜

> **开发中。** 当前构建只确认能编译，还没有在游戏内验证，可能无法加载、崩溃或导致掉线。仅支持国服。

在本地加载游戏里的地图和过场场景，用来自由探索和拍照。目前面向国服客户端开发。

## 安装

在 Dalamud 的自定义插件仓库中添加以下地址，搜索“Wanderlens”安装，输入 `/wlens` 打开窗口。

```text
https://raw.githubusercontent.com/SAD777A/1010/main/pluginmaster.json
```

## 使用前须知

- 先停用原版 HMapSync，两者不能同时启用。首次启动会复制原版的设置，原文件不动。
- 本插件会改动客户端并拦截发往服务器的数据包，有掉线和账号处罚的风险，请自行承担。
- 停用、更新或重载插件前，先用 `/wlens stop` 回到原地。
- 启动时会从 GitHub 下载公开的数据包名称表。联机功能沿用上游，默认指向原作者的中继（需要其密钥），本修改版未做适配和测试。

## 更新记录

- **1.1.1.0**（2026-11-22）：升级 MessagePack 到 2.5.303，修复 [GHSA-qhrr-8q5h-9q3h](https://github.com/advisories/GHSA-qhrr-8q5h-9q3h)。未在游戏内验证。
- **1.1.0.5**（2026-11-22）：首个开发中构建。

## 源码与构建

安装包对应的完整源码在 [source](source)，许可全文见 [LICENSE](LICENSE)，改动记录见 [CN-PORT.md](source/CN-PORT.md)，第三方声明见 [THIRD_PARTY_NOTICES.md](source/THIRD_PARTY_NOTICES.md)。

需要 .NET 10 SDK 和国服 Dalamud 开发文件：

```powershell
cd source
dotnet build HMSync.csproj -c Release
```

## 开发内容

| 内容 | 状态 |
| --- | --- |
| 国服适配、本地加载地图 | 已做基本实测 |
| 剧情进度 / 布局版本切换 | 已在部分地图实测 |
| 副本场地机关与阶段预设 | 已实现，大部分待实测 |
| 区域跳转、环境天气、节日装饰、场景图层 | 已实现，待完整实测 |
| 中文场景名与任务名 | 已实现，待实测 |
| 穿墙速度调节 | 已实现，待实测 |
| 聊天提示汉化 | 未完成 |
| 多人同步 | 未开始适配 |

## 来源与许可

- Wanderlens 是 [HMapSync](https://github.com/Enceladeum/HMapSync)（作者 Enceladeum，AGPL-3.0）的修改版，与原作者无关，相关问题请不要反馈给上游。
- 地图加载、飞行、穿墙、地毯等基础功能来自 HMapSync。
- 场地机关解析、节日名称参考并移植自 [Hyperborea](https://github.com/kawaii/Hyperborea)（BSD-3-Clause）。
- 部分绝本阶段数据来自 [AnoMech](https://github.com/anomek/AnoMech)（AGPL-3.0）。

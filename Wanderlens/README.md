# Wanderlens 漫游镜

> **开发中，暂不提供下载。** 本目录目前只有说明，插件也还没有加入插件仓库列表，在 Dalamud 里搜索不到。

在本地加载游戏里的地图和过场场景，用来自由探索和拍照。目前面向国服客户端开发。

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

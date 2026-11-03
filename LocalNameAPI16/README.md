# LocalName（API16）

## 安装

仅支持卫月 API 16（国际服测试分支）。API 15 请继续使用“本地改名 LocalName”。

在 Dalamud 的自定义插件仓库中添加以下地址，搜索“本地改名 LocalName（API16）”安装；输入 `/localname` 打开设置。与 LocalName 是两个独立插件，不要同时启用。

```text
https://raw.githubusercontent.com/SAD777A/1010/main/pluginmaster.json
```

首次启动时自动导入 LocalName 的设置、预设和本地图片，之后两边分别保存，互不影响。

## 更新日志

### 0.2.45.0

- API 16 独立版，功能与 LocalName 0.2.45.0 相同。
- 适配 API 16：旧 SeString 改为 Lumina 字符串，表格、名牌、聊天、对象名等接口随之更新；改写时保留原有颜色、链接与图标。
- 首次启动自动导入 LocalName 的设置、预设和本地图片。
- 国际服 API 16 测试宿主构建和离线检查通过；尚未在游戏内全面验证，欢迎反馈。

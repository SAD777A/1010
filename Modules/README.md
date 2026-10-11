# LocalName 在线模块

这是用户提供的 Chrysalism 单文件适配版本（0.2.44.5），与独立 LocalName 插件分开分发。

## 安装

在 Chrysalism 的在线模块库中添加以下清单地址，刷新后下载 LocalName，再手动启用：

```text
https://raw.githubusercontent.com/SAD777A/1010/main/ChrysalismModules.json
```

也可添加仓库地址 `https://github.com/SAD777A/1010`，宿主会查找根目录的 ChrysalismModules.json。

启用前请停用独立版 LocalName，并关闭 Chrysalism 的自定义名称，避免冲突。下载后默认关闭；本模块供 Chrysalism 加载，不是 PromeRotation 职业模块，也不要把此清单填进卫月插件仓库列表。

## 文件与更新

LocalName.cs 内嵌程序与素材，约 23.7 MB，首次下载可能需要一些时间。请完整下载，不要手动删改文件末尾的资源数据。在线加载器校验整个文件的 SHA256 后保存到本地，再编译加载。

更新模块时须同步替换文件、版本号和清单中的 SHA256。此模块与独立插件单独打包，独立插件更新不会自动改变这里的模块版本。

发布检查包括原文件一致性、内嵌资源长度及校验值、清单格式和匿名下载一致性；未代替实际游戏内加载验证。

保留原文件的 GPL-3.0-only 标识、作者署名和上游提交记录；许可证见 LICENSE。本目录分发适配加载器及内嵌二进制，完整开发源码继续在私有仓库维护。

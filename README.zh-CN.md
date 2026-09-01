# 字体精简工具 (Font Pruner)

*[English](README.md)*

一个 Unity 编辑器窗口，把 TTF 字体裁剪到只剩游戏里真正用到的那些字。中文字体
通常几十 MB，而 UI 上实际出现的往往只有几百个字——精简后能压到几十 KB。

菜单入口：**Tools → 字体精简工具 (FontPruner)**

## 跟直接敲 sfnttool 命令行的区别

两件命令行不会替你做的事：

- **直接读本地化表。** 勾选 `com.unity.localization` 里要发布的表和语言，字符集
  自动收集。不用手工维护一份 charset.txt，然后眼睁睁看它和实际文案脱节。
- **执行前就告诉你缺哪些字。** 它自己解析源字体的 `cmap`，所以你能提前看到这个
  字体压根没有哪些字符——而不是等出包之后在真机上看到一排豆腐块。

另外它会收拾 `sfnttool` 的烂摊子：sfnttool 会留下孤儿 `vhea` / `VORG` / `BASE`
表，导致 macOS 字体册校验时报 `hmtx` / `vmtx` 可用性错误。

## 环境要求

| | |
|---|---|
| Unity | 2022.3（只在这个版本上验证过；代码没用到更新的 API，更早的 LTS 大概率也能跑） |
| Java | JRE 或 JDK，在 `PATH`、`JAVA_HOME`，或在窗口里手动指定。已验证 Temurin 11 |
| 依赖包 | `com.unity.textmeshpro`、`com.unity.localization`——已声明为依赖，UPM 会自动装 |

输入字体必须是 **TrueType 轮廓的 `.ttf`**。底层 sfnttool 不支持 CFF/OpenType
（`.otf`）轮廓。

## 安装

### UPM（推荐）

Window → Package Manager → **+** → *Add package from git URL*：

```
https://github.com/plgamer/unity-font-pruner.git
```

或者直接写进 `Packages/manifest.json`：

```json
"com.plgamer.fontpruner": "https://github.com/plgamer/unity-font-pruner.git"
```

### 手动拖入

下载仓库，把 `Editor/` 整个复制到工程里当 `Assets/Editor/FontPruner/`。
**`Tools~/` 必须一起带上**——结尾那个 `~` 正是让 Unity 不把 10MB 的 jar 导入成
资产的原因。

两种方式别同时用。同一个工程里存在两份相同的类，编译不过。

## 用法

窗口从上往下走一遍就行。

**① 需要保留的字符。** 手输，或者用预设按钮（数字、大写字母、小写字母、英文
标点、全部可见 ASCII）。*从本地化表收集字符* 会把你勾选的表和语言里的字符全部
提取出来。去重整理、从 txt 导入、导出为 txt 都有。

**② 源字体。** 在 Project 窗口选中 `.ttf`，点 *从当前选中添加*。每个字体下面会
展开一份针对 ① 里字符集的覆盖率报告。

**③ 输出。** 四种模式：

| 模式 | 行为 |
|---|---|
| 独立文件夹 | 输出到工程根目录下的文件夹，完全不动 `Assets` 里的原字体——最稳的默认选项 |
| 同目录 + 后缀 | 输出到原字体旁边，命名成 `MyFont-pruned.ttf` |
| 覆盖源文件 | 就地替换。原文件会先备份到工程根目录 |
| `_Origin` 母本 → 目标 | 把 `MyFont_Origin.ttf` 当作不动的母本，覆盖旁边的 `MyFont.ttf`。字符集变多时可以随时从完整字体重新精简 |

最后这个模式如果打算长期用这工具，值得一开始就配好：完整字体留成 `X_Origin.ttf`，
每次重新精简都是无损的，因为它永远不会拿自己的输出当输入。

**④ 环境。** Java 路径——依次从 `JAVA_HOME`、`PATH`、`/usr/bin/java` 自动探测，
也可以手动指定某个特定 JDK。

然后执行。结果里会逐个列出字体精简前后的体积，另有按钮直接打开输出目录。

## 配置存放

存在 `ProjectSettings/FontPrunerSettings.json`，不进 `Assets`。提交进版本库，
全组就共用同一份字符集和输出配置。

## 许可证

MIT，见 [LICENSE](LICENSE)。

随包分发的 `sfnttool.jar` 是第三方二进制，自带另外的许可证（Apache-2.0、
Unicode/ICU、EPL-1.0、BSD-3-Clause），详见
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

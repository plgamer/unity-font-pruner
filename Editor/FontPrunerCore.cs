using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization.Tables;
using Debug = UnityEngine.Debug;

namespace FontPrunerTool
{
    public enum FontPrunerOutputMode
    {
        // 输出到工程根目录的独立文件夹，不动 Assets 里的原字体
        SeparateFolder = 0,
        // 输出到原字体同目录，文件名加后缀
        SameFolderWithSuffix = 1,
        // 直接覆盖原字体（自动备份）
        OverwriteSource = 2,
        // 源字体是 X_Origin.ttf 这种母本，输出覆盖同目录下的 X.ttf；母本本身不动
        OverwriteOriginTarget = 3,
    }

    /// <summary>
    /// 字体精简工具的配置。存在 ProjectSettings/FontPrunerSettings.json，随工程共享，不进 Assets。
    /// </summary>
    [Serializable]
    public class FontPrunerSettings
    {
        const string kSaveRelativePath = "ProjectSettings/FontPrunerSettings.json";

        // 需要保留的字符
        public string characters = "";

        // 从本地化表收集
        public bool scanLocalization = false;
        public List<string> localizationTables = new List<string>(); // 空 = 全部表
        public List<string> localeCodes = new List<string>();        // 空 = 全部语言

        // 源字体（Assets 相对路径）
        public List<string> fontPaths = new List<string>();

        // 输出
        public FontPrunerOutputMode outputMode = FontPrunerOutputMode.SeparateFolder;
        public string outputFolder = "FontPrunerOutput";
        public string outputSuffix = "-pruned";
        // OverwriteOriginTarget 模式：母本文件名的后缀，输出时去掉它得到目标文件名
        public string originSuffix = "_Origin";
        public bool stripHints = true;
        public bool keepCharsetFile = true;
        // 清掉 sfnttool 留下的 vhea/VORG/BASE 孤儿表，否则系统字体校验会报 hmtx/vmtx 可用性
        public bool stripOrphanTables = true;

        // 环境
        public string javaPath = ""; // 空 = 自动探测（sfnttool 用）
        public string hbSubsetPath = ""; // 空 = 从 PATH 探测；CFF/OTF 轮廓字体用 HarfBuzz hb-subset 精简

        public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        static string SaveFullPath => Path.Combine(ProjectRoot, kSaveRelativePath);

        public static FontPrunerSettings Load()
        {
            try
            {
                if (File.Exists(SaveFullPath))
                {
                    var json = File.ReadAllText(SaveFullPath, Encoding.UTF8);
                    var loaded = JsonUtility.FromJson<FontPrunerSettings>(json);
                    if (loaded != null)
                    {
                        // JsonUtility 对缺失字段会留 null，补齐避免后续 NRE
                        loaded.localizationTables ??= new List<string>();
                        loaded.localeCodes ??= new List<string>();
                        loaded.fontPaths ??= new List<string>();
                        return loaded;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FontPruner] 读取配置失败，改用默认配置：{e.Message}");
            }
            return new FontPrunerSettings();
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(SaveFullPath, JsonUtility.ToJson(this, true), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FontPruner] 保存配置失败：{e.Message}");
            }
        }
    }

    public struct CharsetStats
    {
        // 去重排序后的字符串，即真正写进 charset.txt 的内容
        public string Normalized;
        public int Total;
        public int Cjk;
        public int Ascii;
        public int Other;
        // sfnttool 只输出 BMP cmap，BMP 外字符（emoji 等）会被丢掉
        public int DroppedNonBmp;
        public int DroppedControl;
    }

    public static class FontPrunerCharset
    {
        public const string Digits = "0123456789";
        public const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        public const string Lower = "abcdefghijklmnopqrstuvwxyz";
        public const string AsciiPunctuation = " !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

        public static string AsciiPrintable
        {
            get
            {
                var sb = new StringBuilder();
                for (var c = 0x20; c <= 0x7E; c++) sb.Append((char)c);
                return sb.ToString();
            }
        }

        /// <summary>
        /// 去重 + 按码点排序（输出稳定，方便 diff），并把 sfnttool 用不上的字符剔掉。
        /// </summary>
        public static CharsetStats Normalize(string raw)
        {
            var stats = new CharsetStats();
            var set = new SortedSet<char>();
            if (string.IsNullOrEmpty(raw)) { stats.Normalized = ""; return stats; }

            for (var i = 0; i < raw.Length; i++)
            {
                var c = raw[i];

                // 代理对 = BMP 外字符，sfnttool 只写 WINDOWS_BMP cmap，用不了
                if (char.IsSurrogate(c))
                {
                    if (char.IsHighSurrogate(c) && i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1])) i++;
                    stats.DroppedNonBmp++;
                    continue;
                }

                // 换行/制表/其它控制字符不是字形
                if (c == '\r' || c == '\n' || c == '\t' || char.IsControl(c))
                {
                    stats.DroppedControl++;
                    continue;
                }

                set.Add(c);
            }

            var sb = new StringBuilder(set.Count);
            foreach (var c in set)
            {
                sb.Append(c);
                if (c < 0x80) stats.Ascii++;
                else if (IsCjk(c)) stats.Cjk++;
                else stats.Other++;
            }

            stats.Normalized = sb.ToString();
            stats.Total = set.Count;
            return stats;
        }

        static bool IsCjk(char c)
        {
            return (c >= 0x2E80 && c <= 0x9FFF)     // CJK 部首 / 注音 / 假名 / 统一汉字
                   || (c >= 0xF900 && c <= 0xFAFF)  // 兼容汉字
                   || (c >= 0xFF00 && c <= 0xFFEF); // 全角字符
        }
    }

    public static class FontPrunerLocalization
    {
        public static List<string> GetTableCollectionNames()
        {
            var names = new List<string>();
            try
            {
                foreach (var c in LocalizationEditorSettings.GetStringTableCollections())
                {
                    if (c != null) names.Add(c.TableCollectionName);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FontPruner] 读取本地化表失败：{e.Message}");
            }
            names.Sort();
            return names;
        }

        public static List<string> GetLocaleCodes()
        {
            var codes = new SortedSet<string>();
            try
            {
                foreach (var c in LocalizationEditorSettings.GetStringTableCollections())
                {
                    if (c == null) continue;
                    foreach (var t in c.StringTables)
                    {
                        if (t != null) codes.Add(t.LocaleIdentifier.Code);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FontPruner] 读取本地化语言列表失败：{e.Message}");
            }
            return codes.ToList();
        }

        /// <summary>
        /// 从 String Table 里把所有译文拼起来。tables / locales 为空表示全选。
        /// </summary>
        public static string CollectCharacters(
            ICollection<string> tables, ICollection<string> locales, out int entryCount, out int tableCount)
        {
            var sb = new StringBuilder();
            entryCount = 0;
            tableCount = 0;

            foreach (var collection in LocalizationEditorSettings.GetStringTableCollections())
            {
                if (collection == null) continue;
                if (tables != null && tables.Count > 0 && !tables.Contains(collection.TableCollectionName)) continue;

                foreach (StringTable table in collection.StringTables)
                {
                    if (table == null) continue;
                    var code = table.LocaleIdentifier.Code;
                    if (locales != null && locales.Count > 0 && !locales.Contains(code)) continue;

                    tableCount++;
                    foreach (var entry in table.Values)
                    {
                        if (entry == null || string.IsNullOrEmpty(entry.Value)) continue;
                        sb.Append(entry.Value);
                        entryCount++;
                    }
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 扫描工程内的 ScriptableObject (.asset) 资产，收集其中用到的字符。
    /// 直接读 .asset 的序列化文本而不反序列化资产：速度快、内存稳。
    /// 文本序列化（本项目采用）下，字符串字段（配置表、文案）里的中文都以 UTF-8 原文存在文件里。
    /// </summary>
    public static class FontPrunerScriptableObjects
    {
        public static string CollectCharacters(out int assetCount, out int charCount)
        {
            var found = new SortedSet<char>();
            assetCount = 0;

            var guids = AssetDatabase.FindAssets("t:ScriptableObject");
            for (var i = 0; i < guids.Length; i++)
            {
                if (i % 32 == 0)
                    EditorUtility.DisplayProgressBar(
                        "字体精简", "扫描 ScriptableObject (.asset) 资产…", (float)i / guids.Length);

                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                // 只扫 Assets 下的用户资产；包缓存里的资产对游戏字体没有意义
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal)) continue;

                string text;
                try
                {
                    text = File.ReadAllText(Path.Combine(FontPrunerSettings.ProjectRoot, path));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[FontPruner] 读取 {path} 失败，跳过：{e.Message}");
                    continue;
                }
                assetCount++;

                for (var j = 0; j < text.Length; j++)
                {
                    var c = text[j];

                    // Unity 的 YAML 会把非 ASCII 写成 \uXXXX 转义（如 "1\u7EA7\u5145\u7535\u6869"），
                    // 不解码的话整份中文配置都会漏掉
                    if (c == '\\' && j + 5 < text.Length && text[j + 1] == 'u' && TryHex4(text, j + 2, out var decoded))
                    {
                        found.Add(decoded);
                        j += 5;
                        continue;
                    }

                    // ASCII 由预设覆盖；控制字符不是字形；U+FFFD 是二进制/坏字节的替身
                    if (c < 0x80 || char.IsControl(c) || c == '\uFFFD') continue;
                    found.Add(c);
                }
            }

            EditorUtility.ClearProgressBar();

            var sb = new StringBuilder(found.Count);
            foreach (var c in found) sb.Append(c);
            charCount = sb.Length;
            return sb.ToString();
        }

        static bool TryHex4(string s, int start, out char result)
        {
            var code = 0;
            for (var k = 0; k < 4; k++)
            {
                var d = s[start + k];
                int v;
                if (d >= '0' && d <= '9') v = d - '0';
                else if (d >= 'a' && d <= 'f') v = d - 'a' + 10;
                else if (d >= 'A' && d <= 'F') v = d - 'A' + 10;
                else { result = '\0'; return false; }
                code = (code << 4) | v;
            }
            result = (char)code;
            return true;
        }
    }

    /// <summary>
    /// 扫描运行时 C# 代码里字符串字面量（"" / @"" / $"" / $@""）中的非 ASCII 字符。
    /// 配置表（.asset）覆盖的是"配置驱动"的文案；动态实例化的小字、状态、拼出来的提示
    /// 往往写死在代码里，得靠这个扫。默认只扫游戏运行时目录，跳过注释与编辑器/测试/示例/第三方框架代码。
    /// </summary>
    public static class FontPrunerCSharpCode
    {
        // 默认扫描根（Assets 相对路径）。改成 Null 则回退扫描 Assets 下所有运行时 .cs。
        static readonly string[] kDefaultRoots =
        {
            "Assets/Scripts/GameCore",
            "Assets/Scripts/Gameplay",
        };

        public static string CollectCharacters(out int fileCount, out int charCount)
        {
            var found = new SortedSet<char>();
            fileCount = 0;
            var root = FontPrunerSettings.ProjectRoot;

            foreach (var relRoot in kDefaultRoots)
            {
                var absRoot = Path.Combine(root, relRoot);
                if (!Directory.Exists(absRoot)) continue;

                foreach (var file in Directory.GetFiles(absRoot, "*.cs", SearchOption.AllDirectories))
                {
                    var rel = Path.Combine(relRoot, Path.GetRelativePath(absRoot, file)).Replace('\\', '/');
                    if (ShouldSkip(rel)) continue;

                    string text;
                    try
                    {
                        text = File.ReadAllText(file, Encoding.UTF8);
                    }
                    catch
                    {
                        continue;
                    }
                    fileCount++;
                    CollectFromSource(text, found);
                }
            }

            var sb = new StringBuilder(found.Count);
            foreach (var c in found) sb.Append(c);
            charCount = sb.Length;
            return sb.ToString();
        }

        /// <summary>跳过编辑器 / 测试 / 示例 / 第三方框架目录——它们不进游戏运行时，不会展示给玩家。</summary>
        static bool ShouldSkip(string rel)
        {
            foreach (var seg in rel.Split('/'))
            {
                if (seg == "Editor" || seg == "Tests" || seg == "Test" ||
                    seg == "Samples" || seg == "Example" || seg == "Pipeline")
                    return true;
            }
            return false;
        }

        static void CollectFromSource(string text, SortedSet<char> found)
        {
            var i = 0;
            var n = text.Length;
            while (i < n)
            {
                var c = text[i];

                // 行注释
                if (c == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    i = text.IndexOf('\n', i + 2);
                    if (i < 0) break;
                    i++;
                    continue;
                }
                // 块注释
                if (c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 2;
                    continue;
                }
                // 字符串字面量（处理 @"" 原样字符串与 "" 转义）
                if (c == '"')
                {
                    var verbatim = i > 0 && text[i - 1] == '@';
                    i++;
                    while (i < n)
                    {
                        var sc = text[i];
                        if (sc == '\\' && !verbatim)
                        {
                            i += 2;
                            continue;
                        }
                        if (sc == '"')
                        {
                            if (verbatim && i + 1 < n && text[i + 1] == '"') { i += 2; continue; }
                            i++;
                            break;
                        }
                        if (sc >= 0x80 && !char.IsControl(sc) && sc != '\uFFFD' && !char.IsSurrogate(sc))
                            found.Add(sc);
                        i++;
                    }
                    continue;
                }
                // 字符字面量 ''：跳过内容，防止把单字符标识当字符串收进来
                if (c == '\'')
                {
                    i++;
                    while (i < n)
                    {
                        var sc = text[i];
                        if (sc == '\\') { i += 2; continue; }
                        if (sc == '\'') { i++; break; }
                        i++;
                    }
                    continue;
                }

                i++;
            }
        }
    }

    public static class FontPrunerRunner
    {
        public class Result
        {
            public string FontPath;      // 源字体（Assets 相对路径或绝对路径）
            public string OutputPath;    // 输出字体绝对路径
            public string BackupPath;    // 覆盖模式下的备份路径
            public long OriginalSize;
            public long NewSize;
            public bool Ok;
            public string Message;       // 失败原因 / Java 输出
            public string Warning;       // 成功但有需要提醒的地方（比如源字体缺字）
            public string CharsetPath;   // 跟着输出字体一起放的 charset.txt
            public int CharsCovered;     // 源字体里真正命中的字符数
            public int CharsRequested;
        }

        static string s_ToolsDir;

        /// <summary>
        /// Tools~ 目录（Unity 忽略带 ~ 的文件夹，jar 不会被导入成资产）。
        /// 不写死绝对路径；装成 UPM 包和直接拖进 Assets 两种方式都支持。
        /// </summary>
        public static string ToolsDir
        {
            get
            {
                if (!string.IsNullOrEmpty(s_ToolsDir) && Directory.Exists(s_ToolsDir)) return s_ToolsDir;

                // 1) 装成 UPM 包：AssetDatabase.FindAssets 搜不到包里的脚本（实测命中数为 0），
                //    只能从程序集反查所属包，再用 resolvedPath 取磁盘真实位置。
                //    git URL / registry 装的包躺在 Library/PackageCache 下，
                //    AssetDatabase 给的 "Packages/包名/..." 只是虚拟路径，磁盘上不存在。
                var fromPackage = FindToolsInPackage();
                if (!string.IsNullOrEmpty(fromPackage))
                {
                    s_ToolsDir = fromPackage;
                    return s_ToolsDir;
                }

                // 2) 直接拖进 Assets：这时 AssetDatabase 才能定位到本脚本
                foreach (var guid in AssetDatabase.FindAssets("FontPrunerCore t:MonoScript"))
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!assetPath.EndsWith("/FontPrunerCore.cs", StringComparison.Ordinal)) continue;

                    var dir = Path.GetDirectoryName(Path.Combine(FontPrunerSettings.ProjectRoot, assetPath));
                    var candidate = Path.Combine(dir, "Tools~");
                    if (!Directory.Exists(candidate)) continue;

                    s_ToolsDir = candidate;
                    return s_ToolsDir;
                }

                // 3) 兜底：按约定路径
                s_ToolsDir = Path.Combine(Application.dataPath, "Editor/FontPruner/Tools~");
                return s_ToolsDir;
            }
        }

        /// <summary>
        /// 本程序集若属于某个 UPM 包，就在包根下递归找 Tools~。
        /// 包目录很小，递归代价可忽略，好处是不依赖 Tools~ 在包里的层级。
        /// 不在包里（直接拖进 Assets）时返回 null。
        /// </summary>
        static string FindToolsInPackage()
        {
            var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(FontPrunerRunner).Assembly);
            if (pkg == null || string.IsNullOrEmpty(pkg.resolvedPath) || !Directory.Exists(pkg.resolvedPath))
                return null;

            var hits = Directory.GetDirectories(pkg.resolvedPath, "Tools~", SearchOption.AllDirectories);
            return hits.Length > 0 ? hits[0] : null;
        }

        public static string JarPath => Path.Combine(ToolsDir, "bin/sfnttool.jar");

        public static bool JarExists => File.Exists(JarPath);

        /// <summary>
        /// 按优先级探测 java。source 回填命中来源，便于在 UI 上显示。
        /// </summary>
        public static string ResolveJava(FontPrunerSettings settings, out string source)
        {
            if (settings != null && !string.IsNullOrEmpty(settings.javaPath))
            {
                if (File.Exists(settings.javaPath)) { source = "手动指定"; return settings.javaPath; }
                source = $"手动指定的路径不存在：{settings.javaPath}";
                return null;
            }

            var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(javaHome))
            {
                var p = Path.Combine(javaHome, "bin", JavaExeName);
                if (File.Exists(p)) { source = "JAVA_HOME"; return p; }
            }

            var bundled = Path.Combine(
                EditorApplication.applicationContentsPath,
                "PlaybackEngines/AndroidPlayer/OpenJDK/bin", JavaExeName);
            if (File.Exists(bundled)) { source = "Unity 自带 OpenJDK"; return bundled; }

#if UNITY_EDITOR_WIN
            source = null;
            return null;
#else
            if (File.Exists("/usr/bin/java")) { source = "/usr/bin/java"; return "/usr/bin/java"; }
            var onPath = WhichJava();
            if (onPath != null) { source = "PATH"; return onPath; }
            source = null;
            return null;
#endif
        }

        static string JavaExeName
        {
            get
            {
#if UNITY_EDITOR_WIN
                return "java.exe";
#else
                return "java";
#endif
            }
        }

        static string WhichJava()
        {
            return WhichTool("java");
        }

        /// <summary>
        /// 按优先级探测 hb-subset（HarfBuzz，用于 CFF/OTF 轮廓字体精简）。
        /// source 回填命中来源，便于在 UI 上显示。
        /// </summary>
        public static string ResolveHbSubset(FontPrunerSettings settings, out string source)
        {
            if (settings != null && !string.IsNullOrEmpty(settings.hbSubsetPath))
            {
                if (File.Exists(settings.hbSubsetPath)) { source = "手动指定"; return settings.hbSubsetPath; }
                source = $"手动指定的路径不存在：{settings.hbSubsetPath}";
                return null;
            }
#if UNITY_EDITOR_WIN
            source = null;
            return null; // Windows 需要单独安装 HarfBuzz 并在窗口里手动指定路径
#else
            var found = WhichTool("hb-subset");
            if (found != null) { source = "PATH"; return found; }
            // Homebrew 在非登录 shell 里可能不在 PATH，兜底几个常见装法
            foreach (var p in new[] { "/opt/homebrew/bin/hb-subset", "/usr/local/bin/hb-subset", "/usr/bin/hb-subset" })
            {
                if (File.Exists(p)) { source = p.StartsWith("/usr/bin", StringComparison.Ordinal) ? "系统" : "Homebrew"; return p; }
            }
            source = null;
            return null;
#endif
        }

        static string WhichTool(string name)
        {
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/which", name)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                var stdout = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                return !string.IsNullOrEmpty(stdout) && File.Exists(stdout) ? stdout : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>字体轮廓/容器类型，决定用哪套裁剪工具。</summary>
        public enum OutlineKind
        {
            Unknown,   // 不是有效字体（detail 里带原因）
            Ttc,       // TTC 集合，不支持
            TrueType,  // glyf 轮廓 → sfnttool
            Cff,       // OTTO/CFF 轮廓 → hb-subset
        }

        /// <summary>
        /// 读 sfnt 版本判断轮廓类型。错误时返回 Unknown 并在 reason 里带原因。
        /// </summary>
        public static OutlineKind DetectOutline(string absolutePath, out string reason)
        {
            reason = null;
            try
            {
                if (!File.Exists(absolutePath)) { reason = "文件不存在"; return OutlineKind.Unknown; }
                using var fs = File.OpenRead(absolutePath);
                using var br = new BinaryReader(fs);

                if (fs.Length < 12) { reason = "文件过小，不是有效字体"; return OutlineKind.Unknown; }
                var version = ReadUInt32BE(br);
                if (version == 0x74746366u) return OutlineKind.Ttc;                       // "ttcf"
                if (version == 0x4F54544Fu) return OutlineKind.Cff;                       // "OTTO"
                if (version == 0x00010000u || version == 0x74727565u) return OutlineKind.TrueType;
                reason = "无法识别的字体格式";
                return OutlineKind.Unknown;
            }
            catch (Exception e)
            {
                reason = $"读取字体失败：{e.Message}";
                return OutlineKind.Unknown;
            }
        }

        /// <summary>
        /// 检查字体能否精简。TrueType 与 CFF/OTF 都支持（分别走 sfnttool / hb-subset）；
        /// OK 返回 null，否则返回给用户看的原因。
        /// </summary>
        public static string ValidateFont(string absolutePath)
        {
            var kind = DetectOutline(absolutePath, out var reason);
            switch (kind)
            {
                case OutlineKind.TrueType:
                case OutlineKind.Cff:
                    return null;
                case OutlineKind.Ttc:
                    return "TTC 字体集合不支持，请先拆成单个字体";
                default:
                    return reason ?? "无法识别的字体格式";
            }
        }

        static uint ReadUInt32BE(BinaryReader br)
        {
            var b = br.ReadBytes(4);
            return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        }

        /// <summary>
        /// 把字符写成 charset.txt 并逐个字体调 sfnttool.jar。
        /// </summary>
        public static List<Result> Run(
            FontPrunerSettings settings, string normalizedChars, Action<float, string> onProgress = null)
        {
            var results = new List<Result>();

            var root = FontPrunerSettings.ProjectRoot;

            // 预判这批字体各自需要哪套裁剪工具：
            // TrueType（glyf）→ sfnttool(java)；CFF/OTF（"OTTO"）→ HarfBuzz hb-subset。
            var needSfnttool = false;
            var needHbSubset = false;
            foreach (var fp in settings.fontPaths)
            {
                var k = DetectOutline(ToAbsolute(root, fp), out _);
                if (k == OutlineKind.TrueType) needSfnttool = true;
                else if (k == OutlineKind.Cff) needHbSubset = true;
            }

            if (string.IsNullOrEmpty(normalizedChars))
            {
                results.Add(Fail("(字符)", "字符集为空"));
                return results;
            }
            var java = needSfnttool ? ResolveJava(settings, out _) : null;
            if (needSfnttool && string.IsNullOrEmpty(java))
            {
                results.Add(Fail("(环境)", "需要裁剪 TrueType 字体但找不到 java，请安装 JDK 或在窗口里手动指定 java 路径"));
                return results;
            }
            if (needSfnttool && !JarExists)
            {
                results.Add(Fail("(环境)", $"需要裁剪 TrueType 字体但找不到 sfnttool.jar：{JarPath}"));
                return results;
            }
            if (needHbSubset && string.IsNullOrEmpty(ResolveHbSubset(settings, out _)))
            {
                results.Add(Fail("(环境)",
                    "包含 CFF/OTF 轮廓字体，但找不到 hb-subset（HarfBuzz）。请安装 HarfBuzz 或在窗口里手动指定 hb-subset 路径。"));
                return results;
            }

            // sfnttool 的 EncodingDetect 能正确识别无 BOM 的 UTF-8（含纯中文），不写 BOM
            var charsetFile = Path.Combine(root, "Temp/FontPruner/charset.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(charsetFile));
            File.WriteAllText(charsetFile, normalizedChars, new UTF8Encoding(false));

            var outputFolderAbs = ToAbsolute(root, string.IsNullOrEmpty(settings.outputFolder)
                ? "FontPrunerOutput"
                : settings.outputFolder);

            var backupDir = Path.Combine(root, "FontPrunerBackup", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var touchedAssets = new List<string>();
            // charset.txt 跟着输出字体走：每个真正产出了字体的目录放一份
            var charsetDirs = new HashSet<string>();

            for (var i = 0; i < settings.fontPaths.Count; i++)
            {
                var fontPath = settings.fontPaths[i];
                onProgress?.Invoke((float)i / settings.fontPaths.Count, Path.GetFileName(fontPath));

                var inputAbs = ToAbsolute(root, fontPath);
                var invalid = ValidateFont(inputAbs);
                if (invalid != null)
                {
                    results.Add(Fail(fontPath, invalid));
                    continue;
                }

                // 关键保险：subset 是破坏性的，源字体里没有的字符不可能凭空出现。
                // 一个都命中不到还继续跑，只会产出一个只剩 .notdef 的空字体。
                var coverage = FontPrunerSfnt.CheckCoverage(inputAbs, normalizedChars);
                string coverageWarning = null;
                if (!coverage.CmapUnreadable)
                {
                    if (coverage.Covered == 0)
                    {
                        results.Add(Fail(fontPath,
                            $"源字体里一个目标字符都没有（共 {normalizedChars.Length} 个），继续执行只会产出空字体。\n" +
                            "这通常说明源字体本身已经被精简过了——请改用未精简的完整母本字体。"));
                        continue;
                    }
                    if (!string.IsNullOrEmpty(coverage.Missing))
                    {
                        coverageWarning =
                            $"源字体缺少 {coverage.Missing.Length} 个字符，它们不会出现在结果里：{Ellipsis(coverage.Missing, 40)}";
                    }
                }

                var fileName = Path.GetFileName(inputAbs);
                var nameNoExt = Path.GetFileNameWithoutExtension(inputAbs);
                var ext = Path.GetExtension(inputAbs);

                string finalOutput;
                switch (settings.outputMode)
                {
                    case FontPrunerOutputMode.SameFolderWithSuffix:
                        var suffix = string.IsNullOrEmpty(settings.outputSuffix) ? "-pruned" : settings.outputSuffix;
                        finalOutput = Path.Combine(Path.GetDirectoryName(inputAbs), nameNoExt + suffix + ext);
                        break;
                    case FontPrunerOutputMode.OverwriteSource:
                        finalOutput = inputAbs;
                        break;
                    case FontPrunerOutputMode.OverwriteOriginTarget:
                        var target = ResolveOriginTarget(inputAbs, settings.originSuffix, out var whyNot);
                        if (target == null)
                        {
                            results.Add(Fail(fontPath, whyNot));
                            continue;
                        }
                        finalOutput = target;
                        break;
                    default:
                        Directory.CreateDirectory(outputFolderAbs);
                        finalOutput = Path.Combine(outputFolderAbs, fileName);
                        break;
                }

                if (settings.outputMode != FontPrunerOutputMode.OverwriteSource &&
                    PathsEqual(finalOutput, inputAbs))
                {
                    results.Add(Fail(fontPath, "输出路径和源字体相同，会覆盖原文件；请换目录/后缀，或显式选择「覆盖原字体」模式"));
                    continue;
                }

                // 先写临时文件，成功后再落到最终位置，避免半成品覆盖原字体
                var tempOutput = Path.Combine(root, "Temp/FontPruner", nameNoExt + ".pruned" + ext);
                if (File.Exists(tempOutput)) File.Delete(tempOutput);

                // 按轮廓类型选工具：TrueType 走 sfnttool(java)，CFF/OTF 走 hb-subset
                var outline = DetectOutline(inputAbs, out _);
                string toolName, stdout, stderr;
                int exec;
                if (outline == OutlineKind.Cff)
                {
                    toolName = "hb-subset";
                    var hb = ResolveHbSubset(settings, out _);
                    var sb = new StringBuilder();
                    sb.Append(Quote(inputAbs)).Append(" --output-file=").Append(Quote(tempOutput))
                      .Append(" --text-file=").Append(Quote(charsetFile));
                    if (settings.stripHints) sb.Append(" --no-hinting");
                    exec = Execute(hb, sb.ToString(), root, out stdout, out stderr);
                }
                else
                {
                    toolName = "sfnttool";
                    var args = new StringBuilder();
                    args.Append("-jar ").Append(Quote(JarPath));
                    if (settings.stripHints) args.Append(" -h");
                    // -c 会吞掉到倒数第三个参数为止（SfntTool.java:79），必须放在所有选项之后
                    args.Append(" -c ").Append(Quote(charsetFile))
                        .Append(' ').Append(Quote(inputAbs))
                        .Append(' ').Append(Quote(tempOutput));
                    exec = Execute(java, args.ToString(), root, out stdout, out stderr);
                }

                if (exec != 0 || !File.Exists(tempOutput) || new FileInfo(tempOutput).Length == 0)
                {
                    var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    results.Add(Fail(fontPath, $"{toolName} 执行失败（exit={exec}）\n{detail}"));
                    continue;
                }

                var strippedNote = "";
                if (settings.stripOrphanTables)
                {
                    try
                    {
                        strippedNote = FontPrunerSfnt.CleanOutput(tempOutput).Describe();
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[FontPruner] 清理输出字体失败（字体本身仍可用）：{e.Message}");
                    }
                }

                var result = new Result
                {
                    FontPath = fontPath,
                    OriginalSize = new FileInfo(inputAbs).Length,
                    OutputPath = finalOutput,
                    Ok = true,
                    Message = string.IsNullOrEmpty(strippedNote) ? stdout : strippedNote + "\n" + stdout,
                    Warning = coverageWarning,
                    CharsCovered = coverage.CmapUnreadable ? -1 : coverage.Covered,
                    CharsRequested = normalizedChars.Length,
                };

                try
                {
                    // 覆盖类模式先把即将被顶掉的那个文件备份下来
                    var overwrites = settings.outputMode == FontPrunerOutputMode.OverwriteSource
                                     || settings.outputMode == FontPrunerOutputMode.OverwriteOriginTarget;
                    if (overwrites && File.Exists(finalOutput))
                    {
                        Directory.CreateDirectory(backupDir);
                        result.BackupPath = Path.Combine(backupDir, Path.GetFileName(finalOutput));
                        File.Copy(finalOutput, result.BackupPath, true);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(finalOutput));
                    File.Copy(tempOutput, finalOutput, true);
                    File.Delete(tempOutput);
                }
                catch (Exception e)
                {
                    results.Add(Fail(fontPath, $"写出结果失败：{e.Message}"));
                    continue;
                }

                result.NewSize = new FileInfo(finalOutput).Length;

                // charset.txt 落在输出字体旁边（独立目录模式则落在那个目录里），
                // 这样重烘 TMP 图集时能直接把它当 TextAsset 喂给 Characters from File。
                var charsetDir = settings.outputMode == FontPrunerOutputMode.SeparateFolder
                    ? outputFolderAbs
                    : Path.GetDirectoryName(finalOutput);
                result.CharsetPath = Path.Combine(charsetDir, CharsetFileName);
                if (settings.keepCharsetFile) charsetDirs.Add(charsetDir);

                results.Add(result);

                var assetPath = ToAssetPath(root, finalOutput);
                if (assetPath != null) touchedAssets.Add(assetPath);
            }

            if (settings.keepCharsetFile)
            {
                foreach (var dir in charsetDirs)
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        var dest = Path.Combine(dir, CharsetFileName);
                        File.Copy(charsetFile, dest, true);
                        var destAsset = ToAssetPath(root, dest);
                        if (destAsset != null) touchedAssets.Add(destAsset);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[FontPruner] 写出 {CharsetFileName} 到 {dir} 失败：{e.Message}");
                    }
                }
            }

            onProgress?.Invoke(1f, "");

            if (touchedAssets.Count > 0)
            {
                foreach (var p in touchedAssets) AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();
            }

            return results;
        }

        static Result Fail(string fontPath, string message)
        {
            return new Result { FontPath = fontPath, Ok = false, Message = message };
        }

        public const string DefaultOriginSuffix = "_Origin";
        public const string CharsetFileName = "charset.txt";

        /// <summary>
        /// 把 "…/SourceHanSansCN-Bold_Origin.ttf" 解析成同目录的 "…/SourceHanSansCN-Bold.ttf"。
        /// 文件名不带该后缀时返回 null，并给出原因。
        /// </summary>
        public static string ResolveOriginTarget(string inputAbs, string originSuffix, out string error)
        {
            error = null;
            var suffix = string.IsNullOrEmpty(originSuffix) ? DefaultOriginSuffix : originSuffix;
            var nameNoExt = Path.GetFileNameWithoutExtension(inputAbs);

            if (!nameNoExt.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                error = $"文件名不以「{suffix}」结尾，这个模式不知道该覆盖哪个文件。\n" +
                        $"请把母本命名成 例如 {nameNoExt}{suffix}{Path.GetExtension(inputAbs)}，或换别的输出方式。";
                return null;
            }

            var targetName = nameNoExt.Substring(0, nameNoExt.Length - suffix.Length);
            if (targetName.Length == 0)
            {
                error = $"去掉「{suffix}」之后文件名就空了。";
                return null;
            }

            return Path.Combine(Path.GetDirectoryName(inputAbs), targetName + Path.GetExtension(inputAbs));
        }

        static string Ellipsis(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max) + $" …（共 {s.Length} 个）";
        }

        static int Execute(string exe, string args, string workingDir, out string stdout, out string stderr)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDir,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(psi);
            // 先读完两个流再 WaitForExit，避免管道写满导致的死锁
            var outTask = process.StandardOutput.ReadToEndAsync();
            var errTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            stdout = outTask.Result;
            stderr = errTask.Result;
            return process.ExitCode;
        }

        static string Quote(string path) => "\"" + path + "\"";

        public static string ToAbsolute(string root, string path)
        {
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(root, path));
        }

        /// <summary>路径在 Assets 内则返回 "Assets/..." 形式，否则返回 null。</summary>
        public static string ToAssetPath(string root, string absolutePath)
        {
            var assets = Path.Combine(root, "Assets") + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(absolutePath);
            if (!full.StartsWith(assets, StringComparison.Ordinal)) return null;
            return "Assets/" + full.Substring(assets.Length).Replace(Path.DirectorySeparatorChar, '/');
        }

        static bool PathsEqual(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024L) return $"{bytes / 1024f / 1024f:F2} MB";
            if (bytes >= 1024L) return $"{bytes / 1024f:F1} KB";
            return $"{bytes} B";
        }
    }
}

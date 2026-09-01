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
        public string javaPath = ""; // 空 = 自动探测

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
        /// 靠 AssetDatabase 定位本脚本，不写死绝对路径；Assets 和 UPM 包两种装法都支持。
        /// </summary>
        public static string ToolsDir
        {
            get
            {
                if (!string.IsNullOrEmpty(s_ToolsDir) && Directory.Exists(s_ToolsDir)) return s_ToolsDir;

                foreach (var guid in AssetDatabase.FindAssets("FontPrunerCore t:MonoScript"))
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!assetPath.EndsWith("/FontPrunerCore.cs", StringComparison.Ordinal)) continue;

                    var dir = ResolveOnDisk(assetPath);
                    if (string.IsNullOrEmpty(dir)) continue;

                    var candidate = Path.Combine(dir, "Tools~");
                    if (!Directory.Exists(candidate)) continue;

                    s_ToolsDir = candidate;
                    return s_ToolsDir;
                }

                // 兜底：按约定路径
                s_ToolsDir = Path.Combine(Application.dataPath, "Editor/FontPruner/Tools~");
                return s_ToolsDir;
            }
        }

        /// <summary>
        /// 把 AssetDatabase 的资源路径换算成磁盘真实目录。
        /// Assets/ 下直接拼工程根即可；Packages/ 下必须走 PackageInfo.resolvedPath，
        /// 因为 git URL / registry 装的包实际躺在 Library/PackageCache 里，
        /// AssetDatabase 给的 "Packages/包名/..." 只是虚拟路径，磁盘上并不存在。
        /// </summary>
        static string ResolveOnDisk(string assetPath)
        {
            var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            if (pkg != null && !string.IsNullOrEmpty(pkg.resolvedPath) && !string.IsNullOrEmpty(pkg.assetPath))
            {
                var rel = assetPath.Substring(pkg.assetPath.Length).TrimStart('/');
                return Path.GetDirectoryName(Path.Combine(pkg.resolvedPath, rel));
            }

            return Path.GetDirectoryName(Path.Combine(FontPrunerSettings.ProjectRoot, assetPath));
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
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/which", "java")
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

        /// <summary>
        /// 检查字体是否是 sfnttool 能处理的 TrueType（必须有 glyf 表）。
        /// OK 返回 null，否则返回给用户看的原因。
        /// </summary>
        public static string ValidateFont(string absolutePath)
        {
            try
            {
                if (!File.Exists(absolutePath)) return "文件不存在";
                using var fs = File.OpenRead(absolutePath);
                using var br = new BinaryReader(fs);

                if (fs.Length < 12) return "文件过小，不是有效字体";
                var version = ReadUInt32BE(br);
                if (version == 0x74746366u) return "TTC 字体集合不支持，请先拆成单个 ttf";
                if (version == 0x4F54544Fu) return "OTF/CFF 轮廓字体不支持（sfnttool 只能精简 TrueType glyf 轮廓）";
                if (version != 0x00010000u && version != 0x74727565u) return "无法识别的字体格式";

                var numTables = ReadUInt16BE(br);
                br.ReadBytes(6); // searchRange / entrySelector / rangeShift
                var hasGlyf = false;
                for (var i = 0; i < numTables; i++)
                {
                    if (fs.Position + 16 > fs.Length) break;
                    var tag = Encoding.ASCII.GetString(br.ReadBytes(4));
                    br.ReadBytes(12); // checksum / offset / length
                    if (tag == "glyf") { hasGlyf = true; break; }
                }
                if (!hasGlyf) return "字体没有 glyf 表（可能是 CFF 轮廓），sfnttool 无法精简";
                return null;
            }
            catch (Exception e)
            {
                return $"读取字体失败：{e.Message}";
            }
        }

        static uint ReadUInt32BE(BinaryReader br)
        {
            var b = br.ReadBytes(4);
            return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        }

        static ushort ReadUInt16BE(BinaryReader br)
        {
            var b = br.ReadBytes(2);
            return (ushort)((b[0] << 8) | b[1]);
        }

        /// <summary>
        /// 把字符写成 charset.txt 并逐个字体调 sfnttool.jar。
        /// </summary>
        public static List<Result> Run(
            FontPrunerSettings settings, string normalizedChars, Action<float, string> onProgress = null)
        {
            var results = new List<Result>();

            var java = ResolveJava(settings, out _);
            if (string.IsNullOrEmpty(java))
            {
                results.Add(Fail("(环境)", "找不到 java，请安装 JDK 或在窗口里手动指定 java 路径"));
                return results;
            }
            if (!JarExists)
            {
                results.Add(Fail("(环境)", $"找不到 sfnttool.jar：{JarPath}"));
                return results;
            }
            if (string.IsNullOrEmpty(normalizedChars))
            {
                results.Add(Fail("(字符)", "字符集为空"));
                return results;
            }

            var root = FontPrunerSettings.ProjectRoot;

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

                var args = new StringBuilder();
                args.Append("-jar ").Append(Quote(JarPath));
                if (settings.stripHints) args.Append(" -h");
                // -c 会吞掉到倒数第三个参数为止（SfntTool.java:79），必须放在所有选项之后
                args.Append(" -c ").Append(Quote(charsetFile))
                    .Append(' ').Append(Quote(inputAbs))
                    .Append(' ').Append(Quote(tempOutput));

                var exec = Execute(java, args.ToString(), root, out var stdout, out var stderr);

                if (exec != 0 || !File.Exists(tempOutput) || new FileInfo(tempOutput).Length == 0)
                {
                    var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    results.Add(Fail(fontPath, $"sfnttool 执行失败（exit={exec}）\n{detail}"));
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

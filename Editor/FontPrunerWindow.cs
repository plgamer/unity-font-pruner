using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace FontPrunerTool
{
    /// <summary>
    /// FontPruner 的图形界面：填字符 -> 选字体 -> 执行精简。
    /// 底层调用 Tools~/bin/sfnttool.jar 的 -c（字符 txt）模式，等价于 FontPruner.py 的第三步。
    /// </summary>
    public class FontPrunerWindow : EditorWindow
    {
        FontPrunerSettings _settings;
        Vector2 _scroll;
        Vector2 _charScroll;
        CharsetStats _stats;
        bool _statsDirty = true;

        // 本帧的内容宽度：所有内容都塞进这个固定宽度里，防止长文本把窗口撑宽
        float _contentWidth;

        // 字符输入框固定高度，超出走内部滚动，避免一次粘贴几千字符时控件变得巨长
        const float kCharAreaHeight = 150f;

        static GUIStyle s_WrapTextArea;
        static GUIStyle s_WrapMiniLabel;

        // EditorStyles.textArea 在窗口变宽时会跟着内容变宽，这里强制自动换行
        static GUIStyle WrapTextArea =>
            s_WrapTextArea ??= new GUIStyle(EditorStyles.textArea) { wordWrap = true };

        static GUIStyle WrapMiniLabel =>
            s_WrapMiniLabel ??= new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };

        // 各分区折叠状态
        bool _foldLocalization;
        bool _foldEnv;

        // 本地化表可选项（打开窗口时抓一次）
        List<string> _allTables;
        List<string> _allLocales;

        // 字体合法性缓存，避免每帧重复读文件
        readonly Dictionary<string, string> _fontIssues = new Dictionary<string, string>();

        // 字符覆盖率缓存，key = 字体路径 + 字符集，避免每帧重复解析 cmap
        readonly Dictionary<string, FontPrunerSfnt.Coverage> _coverage =
            new Dictionary<string, FontPrunerSfnt.Coverage>();

        List<FontPrunerRunner.Result> _results;
        string _lastCharsetPreview;

        [MenuItem("Tools/字体精简工具 (FontPruner)")]
        public static void Open()
        {
            var win = GetWindow<FontPrunerWindow>("字体精简");
            win.minSize = new Vector2(560, 620);
        }

        void OnEnable()
        {
            _settings = FontPrunerSettings.Load();
            _statsDirty = true;
        }

        void OnDisable()
        {
            _settings?.Save();
        }

        void OnGUI()
        {
            if (_settings == null) _settings = FontPrunerSettings.Load();

            // 预留竖向滚动条 + 左右边距，内容锁死在这个宽度内
            _contentWidth = Mathf.Max(240f, position.width - 24f);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            // 固定宽度容器：任何子元素都不能靠内容长度把布局撑宽
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(_contentWidth)))
            {
                EditorGUILayout.HelpBox(
                    "把需要保留的字符填进下面的文本框（替代原工具手写的 txt），选好源字体后点执行。\n" +
                    "注意：sfnttool 会移除 GPOS/GSUB/kern 等表，且只输出 BMP cmap —— 阿拉伯语/泰语/天城文这类需要复杂整形的字体不要用它精简；" +
                    "emoji 等 BMP 外字符也会被丢弃。",
                    MessageType.Info);

                DrawCharactersSection();
                EditorGUILayout.Space(8);
                DrawFontsSection();
                EditorGUILayout.Space(8);
                DrawOutputSection();
                EditorGUILayout.Space(8);
                DrawEnvSection();
                EditorGUILayout.Space(10);
                DrawRunSection();
                DrawResultsSection();
            }

            EditorGUILayout.EndScrollView();
        }

        // ---------------------------------------------------------------- 字符

        void DrawCharactersSection()
        {
            Header("① 需要保留的字符");

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("追加预设：", GUILayout.Width(64));
                    if (GUILayout.Button("数字")) AppendChars(FontPrunerCharset.Digits);
                    if (GUILayout.Button("大写字母")) AppendChars(FontPrunerCharset.Upper);
                    if (GUILayout.Button("小写字母")) AppendChars(FontPrunerCharset.Lower);
                    if (GUILayout.Button("英文标点")) AppendChars(FontPrunerCharset.AsciiPunctuation);
                    if (GUILayout.Button("全部可见 ASCII")) AppendChars(FontPrunerCharset.AsciiPrintable);
                }

                // 固定高度 + 内部滚动 + 强制换行：粘一大段单行文本只会往下换行，不会把窗口撑宽
                _charScroll = EditorGUILayout.BeginScrollView(
                    _charScroll, false, false,
                    GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView,
                    GUILayout.Height(kCharAreaHeight));

                EditorGUI.BeginChangeCheck();
                var text = EditorGUILayout.TextArea(
                    _settings.characters, WrapTextArea,
                    GUILayout.ExpandHeight(true), GUILayout.MinHeight(kCharAreaHeight - 6f));
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.characters = text;
                    _statsDirty = true;
                }

                EditorGUILayout.EndScrollView();

                if (_statsDirty)
                {
                    _stats = FontPrunerCharset.Normalize(_settings.characters);
                    _statsDirty = false;
                }

                EditorGUILayout.LabelField(
                    $"去重后 {_stats.Total} 个字符（中日韩 {_stats.Cjk} / ASCII {_stats.Ascii} / 其它 {_stats.Other}）",
                    EditorStyles.miniLabel);

                if (_stats.DroppedNonBmp > 0)
                {
                    EditorGUILayout.HelpBox(
                        $"已丢弃 {_stats.DroppedNonBmp} 个 BMP 外字符（emoji 等），sfnttool 只输出 BMP cmap，无法包含它们。",
                        MessageType.Warning);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("去重整理文本框")) NormalizeInPlace();
                    if (GUILayout.Button("从 txt 导入")) ImportFromTxt();
                    if (GUILayout.Button("导出为 txt")) ExportToTxt();
                    if (GUILayout.Button("清空")) { _settings.characters = ""; _statsDirty = true; }
                }

                EditorGUILayout.Space(4);
                DrawLocalizationSubSection();
            }
        }

        void DrawLocalizationSubSection()
        {
            _foldLocalization = EditorGUILayout.Foldout(_foldLocalization, "从本地化表收集字符", true);
            if (!_foldLocalization) return;

            using (new EditorGUI.IndentLevelScope())
            {
                if (_allTables == null)
                {
                    _allTables = FontPrunerLocalization.GetTableCollectionNames();
                    _allLocales = FontPrunerLocalization.GetLocaleCodes();
                }

                if (_allTables.Count == 0)
                {
                    EditorGUILayout.HelpBox("工程里没找到 String Table Collection。", MessageType.None);
                    return;
                }

                EditorGUILayout.LabelField("表（不勾选任何项 = 全部表）", EditorStyles.miniBoldLabel);
                DrawToggleList(_allTables, _settings.localizationTables);

                EditorGUILayout.LabelField("语言（不勾选任何项 = 全部语言）", EditorStyles.miniBoldLabel);
                DrawToggleList(_allLocales, _settings.localeCodes);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("收集并合并到上面的文本框"))
                    {
                        var collected = FontPrunerLocalization.CollectCharacters(
                            _settings.localizationTables, _settings.localeCodes,
                            out var entryCount, out var tableCount);
                        AppendChars(collected);
                        NormalizeInPlace();
                        Debug.Log($"[FontPruner] 从 {tableCount} 张表的 {entryCount} 条译文里收集字符，" +
                                  $"合并去重后共 {_stats.Total} 个。");
                    }
                    if (GUILayout.Button("刷新列表", GUILayout.Width(80)))
                    {
                        _allTables = null;
                        _allLocales = null;
                    }
                }
            }
        }

        static void DrawToggleList(List<string> all, List<string> selected)
        {
            const int columns = 4;
            for (var i = 0; i < all.Count; i += columns)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (var j = i; j < Mathf.Min(i + columns, all.Count); j++)
                    {
                        var name = all[j];
                        var on = selected.Contains(name);
                        var next = EditorGUILayout.ToggleLeft(name, on);
                        if (next == on) continue;
                        if (next) selected.Add(name);
                        else selected.Remove(name);
                    }
                }
            }
        }

        void AppendChars(string chars)
        {
            if (string.IsNullOrEmpty(chars)) return;
            _settings.characters += chars;
            _statsDirty = true;
        }

        void NormalizeInPlace()
        {
            _stats = FontPrunerCharset.Normalize(_settings.characters);
            _settings.characters = _stats.Normalized;
            _statsDirty = false;
            GUI.FocusControl(null);
        }

        void ImportFromTxt()
        {
            var path = EditorUtility.OpenFilePanel("选择字符 txt", FontPrunerSettings.ProjectRoot, "txt");
            if (string.IsNullOrEmpty(path)) return;
            AppendChars(File.ReadAllText(path));
            NormalizeInPlace();
        }

        void ExportToTxt()
        {
            var stats = FontPrunerCharset.Normalize(_settings.characters);
            if (stats.Total == 0)
            {
                EditorUtility.DisplayDialog("字体精简", "字符集为空，没什么可导出的。", "好");
                return;
            }
            var path = EditorUtility.SaveFilePanel(
                "导出字符 txt", FontPrunerSettings.ProjectRoot, "charset", "txt");
            if (string.IsNullOrEmpty(path)) return;
            File.WriteAllText(path, stats.Normalized, new UTF8Encoding(false));
            Debug.Log($"[FontPruner] 已导出 {stats.Total} 个字符到 {path}\n" +
                      "（可直接喂给 TMP Font Asset Creator 的 Characters from File）");
        }

        // ---------------------------------------------------------------- 字体

        void DrawFontsSection()
        {
            Header("② 源字体（.ttf，TrueType 轮廓）");

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var removeAt = -1;
                for (var i = 0; i < _settings.fontPaths.Count; i++)
                {
                    var path = _settings.fontPaths[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var font = AssetDatabase.LoadAssetAtPath<Font>(path);
                        EditorGUI.BeginChangeCheck();
                        var next = (Font)EditorGUILayout.ObjectField(font, typeof(Font), false);
                        if (EditorGUI.EndChangeCheck())
                        {
                            var nextPath = next == null ? null : AssetDatabase.GetAssetPath(next);
                            if (string.IsNullOrEmpty(nextPath)) removeAt = i;
                            else _settings.fontPaths[i] = nextPath;
                            _fontIssues.Clear();
                        }
                        if (font == null) EditorGUILayout.LabelField(path, WrapMiniLabel);
                        if (GUILayout.Button("移除", GUILayout.Width(48))) removeAt = i;
                    }

                    var issue = GetFontIssue(path);
                    if (issue != null)
                    {
                        EditorGUILayout.HelpBox(issue, MessageType.Error);
                    }
                    else
                    {
                        DrawCoverage(path);
                    }
                }
                if (removeAt >= 0)
                {
                    _settings.fontPaths.RemoveAt(removeAt);
                    _fontIssues.Clear();
                }

                if (_settings.fontPaths.Count == 0)
                {
                    EditorGUILayout.LabelField("（还没有添加字体）", EditorStyles.miniLabel);
                }

                EditorGUILayout.Space(2);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("拖入添加：", GUILayout.Width(64));
                    var dropped = EditorGUILayout.ObjectField(null, typeof(Object), false);
                    if (dropped != null) AddFontFromObject(dropped);
                    if (GUILayout.Button("从当前选中添加", GUILayout.Width(110))) AddFromSelection();
                    if (GUILayout.Button("清空", GUILayout.Width(48)))
                    {
                        _settings.fontPaths.Clear();
                        _fontIssues.Clear();
                    }
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button($"添加工程内所有 *{OriginSuffix} 母本字体")) AddAllOriginFonts();
                }
                EditorGUILayout.LabelField(
                    "可以直接拖 TMP 字体资源（会自动取它的 Source Font File）。", EditorStyles.miniLabel);
            }
        }

        /// <summary>
        /// 执行前就把「源字体里有没有这些字」摊开给用户看 —— 对着已经精简过的字体再精简会得到空字体。
        /// </summary>
        void DrawCoverage(string assetPath)
        {
            if (_stats.Total == 0) return;

            var key = assetPath + " " + _stats.Normalized;
            if (!_coverage.TryGetValue(key, out var cov))
            {
                if (_coverage.Count > 32) _coverage.Clear();
                cov = FontPrunerSfnt.CheckCoverage(
                    FontPrunerRunner.ToAbsolute(FontPrunerSettings.ProjectRoot, assetPath), _stats.Normalized);
                _coverage[key] = cov;
            }

            if (cov.CmapUnreadable)
            {
                EditorGUILayout.LabelField("  （读不出 cmap，跳过缺字检查）", WrapMiniLabel);
                return;
            }

            var missing = cov.Missing == null ? 0 : cov.Missing.Length;
            if (missing == 0)
            {
                EditorGUILayout.LabelField($"  ✓ {_stats.Total} 个字符全部命中", WrapMiniLabel);
            }
            else if (cov.Covered == 0)
            {
                EditorGUILayout.HelpBox(
                    "这个字体里一个目标字符都没有，执行会被拒绝。\n" +
                    "通常说明它本身已经是精简过的字体了 —— 请换未精简的完整母本。",
                    MessageType.Error);
            }
            else
            {
                var preview = cov.Missing;
                string codePoints = ToCodePointString(preview);

                EditorGUILayout.SelectableLabel(
                    $"缺少：{preview}\n码点：{codePoints}",
                    GUILayout.Height(40)
                );
            }
        }

        string GetFontIssue(string assetPath)
        {
            if (_fontIssues.TryGetValue(assetPath, out var cached)) return cached;
            var abs = FontPrunerRunner.ToAbsolute(FontPrunerSettings.ProjectRoot, assetPath);
            var issue = FontPrunerRunner.ValidateFont(abs);
            _fontIssues[assetPath] = issue;
            return issue;
        }

        void AddFromSelection()
        {
            foreach (var obj in Selection.objects) AddFontFromObject(obj);
        }

        /// <summary>把工程里所有 *_Origin 母本字体一次加进来。</summary>
        void AddAllOriginFonts()
        {
            var added = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Font"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var nameNoExt = Path.GetFileNameWithoutExtension(path);
                if (!nameNoExt.EndsWith(OriginSuffix, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (_settings.fontPaths.Contains(path)) continue;
                _settings.fontPaths.Add(path);
                added++;
            }
            _settings.fontPaths.Sort(System.StringComparer.Ordinal);
            _fontIssues.Clear();
            Debug.Log($"[FontPruner] 添加了 {added} 个 *{OriginSuffix} 母本字体" +
                      $"（当前列表共 {_settings.fontPaths.Count} 个）。");
        }

        void AddFontFromObject(Object obj)
        {
            string path = null;
            if (obj is Font font)
            {
                path = AssetDatabase.GetAssetPath(font);
            }
            else if (obj is TMP_FontAsset tmp)
            {
                if (tmp.sourceFontFile != null) path = AssetDatabase.GetAssetPath(tmp.sourceFontFile);
                if (string.IsNullOrEmpty(path))
                {
                    Debug.LogWarning($"[FontPruner] TMP 字体资源 {tmp.name} 没有关联的 Source Font File，" +
                                     "请手动把对应的 .ttf 拖进来。");
                    return;
                }
            }

            if (string.IsNullOrEmpty(path)) return;
            if (_settings.fontPaths.Contains(path)) return;
            _settings.fontPaths.Add(path);
            _fontIssues.Clear();
        }

        // ---------------------------------------------------------------- 输出

        void DrawOutputSection()
        {
            Header("③ 输出");

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _settings.outputMode = (FontPrunerOutputMode)EditorGUILayout.Popup(
                    "输出方式", (int)_settings.outputMode,
                    new[]
                    {
                        "输出到独立目录",
                        "原字体同目录 + 后缀",
                        "覆盖原字体（自动备份）",
                        "母本 X_Origin → 覆盖同目录 X（自动备份）",
                    });

                switch (_settings.outputMode)
                {
                    case FontPrunerOutputMode.SeparateFolder:
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            _settings.outputFolder = EditorGUILayout.TextField("输出目录", _settings.outputFolder);
                            if (GUILayout.Button("浏览", GUILayout.Width(48)))
                            {
                                var picked = EditorUtility.SaveFolderPanel(
                                    "选择输出目录", FontPrunerSettings.ProjectRoot, "FontPrunerOutput");
                                if (!string.IsNullOrEmpty(picked)) _settings.outputFolder = picked;
                            }
                        }
                        EditorGUILayout.LabelField(
                            "相对路径以工程根目录为基准：" +
                            FontPrunerRunner.ToAbsolute(FontPrunerSettings.ProjectRoot, _settings.outputFolder),
                            WrapMiniLabel);
                        break;

                    case FontPrunerOutputMode.SameFolderWithSuffix:
                        _settings.outputSuffix = EditorGUILayout.TextField("文件名后缀", _settings.outputSuffix);
                        break;

                    case FontPrunerOutputMode.OverwriteSource:
                        EditorGUILayout.HelpBox(
                            "会直接替换 Assets 里的源字体文件。执行前会把原文件备份到工程根目录的 " +
                            "FontPrunerBackup/<时间戳>/ 下。\n" +
                            "字体被精简后，引用它的 TMP 静态图集需要手动重新烘一次。",
                            MessageType.Warning);
                        break;

                    case FontPrunerOutputMode.OverwriteOriginTarget:
                        _settings.originSuffix = EditorGUILayout.TextField("母本后缀", _settings.originSuffix);
                        EditorGUILayout.HelpBox(
                            "拿 X" + OriginSuffix + " 当母本，产出覆盖同目录下的 X —— 母本本身一个字节都不动，" +
                            "所以随时可以改字符集重跑，不会像「覆盖原字体」那样越跑越空。\n" +
                            "被顶掉的旧 X 会先备份到 FontPrunerBackup/<时间戳>/。TMP 静态图集仍需手动重烘。",
                            MessageType.Info);
                        DrawOriginTargetPreview();
                        break;
                }

                _settings.stripHints = EditorGUILayout.ToggleLeft(
                    "去除 hinting 信息（-h，体积更小）", _settings.stripHints);
                _settings.keepCharsetFile = EditorGUILayout.ToggleLeft(
                    "把 charset.txt 放到输出字体所在目录", _settings.keepCharsetFile);
                if (_settings.keepCharsetFile)
                {
                    EditorGUILayout.LabelField(
                        _settings.outputMode == FontPrunerOutputMode.SeparateFolder
                            ? "  → 放在上面那个输出目录里"
                            : "  → 跟输出字体同目录；落在 Assets 内会被导入成 TextAsset，" +
                              "可以直接喂给 TMP Font Asset Creator 的 Characters from File",
                        WrapMiniLabel);
                }
                _settings.stripOrphanTables = EditorGUILayout.ToggleLeft(
                    "修正输出字体（清孤儿表 vhea/VORG/BASE + 修 head 校验和）",
                    _settings.stripOrphanTables);
                if (!_settings.stripOrphanTables)
                {
                    EditorGUILayout.HelpBox(
                        "关掉后输出的是 sfnttool 的原始产物：会残留 vhea（vmtx 已被删）、VORG、BASE 三张孤儿表，" +
                        "且 head.checkSumAdjustment 是错的。装进系统时 Font Book 校验会报错（如 hmtx 可用性）。" +
                        "Unity/FreeType 不在乎这些，但没理由关掉。",
                        MessageType.Warning);
                }
            }
        }

        string OriginSuffix => string.IsNullOrEmpty(_settings.originSuffix)
            ? FontPrunerRunner.DefaultOriginSuffix
            : _settings.originSuffix;

        /// <summary>把「谁会被覆盖成什么」摊开给用户看，别让他执行完才发现覆盖错了文件。</summary>
        void DrawOriginTargetPreview()
        {
            if (_settings.fontPaths.Count == 0) return;

            EditorGUILayout.LabelField("将要覆盖：", EditorStyles.miniBoldLabel);
            foreach (var path in _settings.fontPaths)
            {
                var abs = FontPrunerRunner.ToAbsolute(FontPrunerSettings.ProjectRoot, path);
                var target = FontPrunerRunner.ResolveOriginTarget(abs, OriginSuffix, out var error);
                if (target == null)
                {
                    EditorGUILayout.HelpBox($"{Path.GetFileName(path)}\n{error}", MessageType.Error);
                    continue;
                }

                var targetAsset = FontPrunerRunner.ToAssetPath(FontPrunerSettings.ProjectRoot, target);
                var exists = File.Exists(target);
                EditorGUILayout.LabelField(
                    $"  {Path.GetFileName(abs)}  →  {Path.GetFileName(target)}" +
                    (exists ? "" : "（目标不存在，会新建）"),
                    WrapMiniLabel);
                if (exists && targetAsset == null)
                    EditorGUILayout.LabelField("    注意：目标在 Assets 之外", WrapMiniLabel);
            }
        }

        // ---------------------------------------------------------------- 环境

        void DrawEnvSection()
        {
            _foldEnv = EditorGUILayout.Foldout(_foldEnv, "④ 环境（Java / sfnttool.jar）", true);
            if (!_foldEnv) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var java = FontPrunerRunner.ResolveJava(_settings, out var source);
                if (string.IsNullOrEmpty(java))
                {
                    EditorGUILayout.HelpBox(
                        "找不到 java。" + (source ?? "请安装 JDK，或在下面手动指定 java 可执行文件路径。"),
                        MessageType.Error);
                }
                else
                {
                    EditorGUILayout.LabelField($"Java（来源：{source}）", EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField(java, WrapMiniLabel);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    _settings.javaPath = EditorGUILayout.TextField("手动指定 java", _settings.javaPath);
                    if (GUILayout.Button("浏览", GUILayout.Width(48)))
                    {
                        var picked = EditorUtility.OpenFilePanel("选择 java 可执行文件", "/usr/bin", "");
                        if (!string.IsNullOrEmpty(picked)) _settings.javaPath = picked;
                    }
                    if (GUILayout.Button("自动", GUILayout.Width(48))) _settings.javaPath = "";
                }

                if (FontPrunerRunner.JarExists)
                {
                    EditorGUILayout.LabelField("sfnttool.jar", EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField(FontPrunerRunner.JarPath, WrapMiniLabel);
                }
                else
                {
                    EditorGUILayout.HelpBox($"找不到 sfnttool.jar：{FontPrunerRunner.JarPath}", MessageType.Error);
                }
            }
        }

        // ---------------------------------------------------------------- 执行

        void DrawRunSection()
        {
            if (_statsDirty)
            {
                _stats = FontPrunerCharset.Normalize(_settings.characters);
                _statsDirty = false;
            }

            var java = FontPrunerRunner.ResolveJava(_settings, out _);
            var blocked = _stats.Total == 0
                          || _settings.fontPaths.Count == 0
                          || string.IsNullOrEmpty(java)
                          || !FontPrunerRunner.JarExists;

            using (new EditorGUI.DisabledScope(blocked))
            {
                if (GUILayout.Button($"执行精简（{_stats.Total} 个字符 × {_settings.fontPaths.Count} 个字体）",
                        GUILayout.Height(34)))
                {
                    Run();
                }
            }

            if (blocked)
            {
                var reasons = new List<string>();
                if (_stats.Total == 0) reasons.Add("字符集为空");
                if (_settings.fontPaths.Count == 0) reasons.Add("没有添加字体");
                if (string.IsNullOrEmpty(java)) reasons.Add("找不到 java");
                if (!FontPrunerRunner.JarExists) reasons.Add("找不到 sfnttool.jar");
                EditorGUILayout.LabelField("暂不能执行：" + string.Join("、", reasons), EditorStyles.miniLabel);
            }
        }

        void Run()
        {
            if (_settings.outputMode == FontPrunerOutputMode.OverwriteSource)
            {
                var names = string.Join("\n", _settings.fontPaths.Select(Path.GetFileName));
                if (!EditorUtility.DisplayDialog(
                        "确认覆盖原字体",
                        $"下面 {_settings.fontPaths.Count} 个字体文件会被精简后的版本覆盖：\n\n{names}\n\n" +
                        "原文件会先备份到 FontPrunerBackup/<时间戳>/。确定继续？",
                        "覆盖", "取消"))
                {
                    return;
                }
            }
            else if (_settings.outputMode == FontPrunerOutputMode.OverwriteOriginTarget)
            {
                var lines = _settings.fontPaths.Select(p =>
                {
                    var abs = FontPrunerRunner.ToAbsolute(FontPrunerSettings.ProjectRoot, p);
                    var t = FontPrunerRunner.ResolveOriginTarget(abs, OriginSuffix, out _);
                    return t == null
                        ? $"{Path.GetFileName(abs)}  →  ？（文件名没有 {OriginSuffix} 后缀，会被跳过）"
                        : $"{Path.GetFileName(abs)}  →  {Path.GetFileName(t)}";
                });
                if (!EditorUtility.DisplayDialog(
                        "确认覆盖",
                        "会按下面的对应关系覆盖（母本本身不动）：\n\n" + string.Join("\n", lines) +
                        "\n\n被顶掉的旧文件会先备份到 FontPrunerBackup/<时间戳>/。确定继续？",
                        "覆盖", "取消"))
                {
                    return;
                }
            }

            _settings.Save();
            _lastCharsetPreview = _stats.Normalized;

            try
            {
                _results = FontPrunerRunner.Run(_settings, _stats.Normalized, (p, name) =>
                    EditorUtility.DisplayProgressBar("字体精简", $"正在处理 {name}", p));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            _fontIssues.Clear();

            foreach (var r in _results)
            {
                if (r.Ok)
                {
                    Debug.Log($"[FontPruner] {Path.GetFileName(r.FontPath)}：" +
                              $"{FontPrunerRunner.FormatSize(r.OriginalSize)} → " +
                              $"{FontPrunerRunner.FormatSize(r.NewSize)}" +
                              $"（命中字符 {r.CharsCovered}/{r.CharsRequested}）\n输出：{r.OutputPath}" +
                              (string.IsNullOrEmpty(r.BackupPath) ? "" : $"\n备份：{r.BackupPath}") +
                              (string.IsNullOrEmpty(r.Warning) ? "" : $"\n{r.Warning}"));
                }
                else
                {
                    Debug.LogError($"[FontPruner] {r.FontPath} 精简失败：{r.Message}");
                }
            }
        }

        void DrawResultsSection()
        {
            if (_results == null || _results.Count == 0) return;

            EditorGUILayout.Space(8);
            Header("结果");

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                foreach (var r in _results)
                {
                    if (r.Ok)
                    {
                        var ratio = r.OriginalSize > 0 ? r.NewSize * 100f / r.OriginalSize : 0f;
                        EditorGUILayout.LabelField(
                            Path.GetFileName(r.FontPath),
                            $"{FontPrunerRunner.FormatSize(r.OriginalSize)} → " +
                            $"{FontPrunerRunner.FormatSize(r.NewSize)}（{ratio:F2}%）");
                        if (r.CharsRequested > 0 && r.CharsCovered >= 0)
                            EditorGUILayout.LabelField(
                                $"  命中字符 {r.CharsCovered}/{r.CharsRequested}", WrapMiniLabel);
                        EditorGUILayout.LabelField("  输出：" + r.OutputPath, WrapMiniLabel);
                        if (_settings.keepCharsetFile && !string.IsNullOrEmpty(r.CharsetPath))
                            EditorGUILayout.LabelField("  字符表：" + r.CharsetPath, WrapMiniLabel);
                        if (!string.IsNullOrEmpty(r.BackupPath))
                            EditorGUILayout.LabelField("  备份：" + r.BackupPath, WrapMiniLabel);
                        if (!string.IsNullOrEmpty(r.Warning))
                            EditorGUILayout.HelpBox(r.Warning, MessageType.Warning);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox($"{r.FontPath}\n{r.Message}", MessageType.Error);
                    }
                }

                if (!string.IsNullOrEmpty(_lastCharsetPreview))
                {
                    EditorGUILayout.LabelField(
                        $"本次使用的字符集（{_lastCharsetPreview.Length} 个）：", EditorStyles.miniBoldLabel);
                    EditorGUILayout.SelectableLabel(
                        _lastCharsetPreview,
                        EditorStyles.wordWrappedMiniLabel, GUILayout.MaxHeight(60));
                }

                if (GUILayout.Button("打开输出目录"))
                {
                    var target = _results.FirstOrDefault(x => x.Ok)?.OutputPath;
                    if (!string.IsNullOrEmpty(target)) EditorUtility.RevealInFinder(target);
                }
            }
        }

        static void Header(string title)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

    private static string ToCodePointString(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var result = new StringBuilder();

            for (int i = 0; i < text.Length; i++)
            {
                int codePoint;

                if (char.IsHighSurrogate(text[i]) &&
                    i + 1 < text.Length &&
                    char.IsLowSurrogate(text[i + 1]))
                {
                    codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }
                else
                {
                    codePoint = text[i];
                }

                if (result.Length > 0)
                    result.Append(' ');

                result.Append($"U+{codePoint:X4}");
            }

            return result.ToString();
        }
    }
    }

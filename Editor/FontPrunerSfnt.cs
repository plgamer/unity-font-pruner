using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FontPrunerTool
{
    /// <summary>
    /// 直接读写 sfnt（ttf）二进制的小工具：
    /// 1. 查源字体的 cmap，执行前就知道哪些字符压根不在字体里；
    /// 2. 清掉 sfnttool 留下的孤儿表，让系统字体校验（Font Book）不再报 hmtx/vmtx 可用性。
    /// </summary>
    public static class FontPrunerSfnt
    {
        public class TableEntry
        {
            public string Tag;
            public uint Checksum;
            public uint Offset;
            public uint Length;
        }

        // ------------------------------------------------------------ 读表目录

        public static List<TableEntry> ReadTableDirectory(byte[] d)
        {
            var list = new List<TableEntry>();
            if (d.Length < 12) return list;
            var numTables = ReadUInt16(d, 4);
            for (var i = 0; i < numTables; i++)
            {
                var o = 12 + 16 * i;
                if (o + 16 > d.Length) break;
                list.Add(new TableEntry
                {
                    Tag = System.Text.Encoding.ASCII.GetString(d, o, 4),
                    Checksum = ReadUInt32(d, o + 4),
                    Offset = ReadUInt32(d, o + 8),
                    Length = ReadUInt32(d, o + 12),
                });
            }
            return list;
        }

        // ------------------------------------------------------------ cmap 查询

        /// <summary>源字体的 cmap 查询器，只关心 BMP（sfnttool 也只输出 BMP cmap）。</summary>
        public class CmapLookup
        {
            byte[] _d;
            int _format;
            int _sub;

            // format 4
            int _segCount;
            int _endOff, _startOff, _deltaOff, _rangeOff;

            // format 12
            uint _nGroups;
            int _groupsOff;

            public bool Valid => _format == 4 || _format == 12;

            public static CmapLookup Load(string path)
            {
                try
                {
                    var d = File.ReadAllBytes(path);
                    var tables = ReadTableDirectory(d);
                    var cmap = tables.FirstOrDefault(t => t.Tag == "cmap");
                    if (cmap == null) return null;

                    var co = (int)cmap.Offset;
                    var n = ReadUInt16(d, co + 2);

                    // 优先 format 12（(3,10) / (0,4) / (0,6)），退回 format 4（(3,1) / (0,3)）
                    int best = -1, bestScore = -1;
                    for (var i = 0; i < n; i++)
                    {
                        var eo = co + 4 + 8 * i;
                        if (eo + 8 > d.Length) break;
                        var plat = ReadUInt16(d, eo);
                        var enc = ReadUInt16(d, eo + 2);
                        var sub = co + (int)ReadUInt32(d, eo + 4);
                        if (sub + 4 > d.Length) continue;
                        var fmt = ReadUInt16(d, sub);
                        if (fmt != 4 && fmt != 12) continue;

                        var score = fmt == 12 ? 40 : 20;
                        if (plat == 3 && (enc == 10 || enc == 1)) score += 8;
                        else if (plat == 0) score += 4;
                        if (score > bestScore) { bestScore = score; best = sub; }
                    }
                    if (best < 0) return null;

                    var lookup = new CmapLookup { _d = d, _sub = best, _format = ReadUInt16(d, best) };
                    if (lookup._format == 4)
                    {
                        var segX2 = ReadUInt16(d, best + 6);
                        lookup._segCount = segX2 / 2;
                        lookup._endOff = best + 14;
                        lookup._startOff = best + 16 + segX2;
                        lookup._deltaOff = best + 16 + 2 * segX2;
                        lookup._rangeOff = best + 16 + 3 * segX2;
                    }
                    else
                    {
                        lookup._nGroups = ReadUInt32(d, best + 12);
                        lookup._groupsOff = best + 16;
                    }
                    return lookup;
                }
                catch
                {
                    return null;
                }
            }

            public int GetGlyphId(char c)
            {
                return _format == 4 ? Format4(c) : Format12(c);
            }

            int Format4(char c)
            {
                for (var i = 0; i < _segCount; i++)
                {
                    var end = ReadUInt16(_d, _endOff + 2 * i);
                    if (c > end) continue;
                    var start = ReadUInt16(_d, _startOff + 2 * i);
                    if (c < start) return 0;

                    var delta = (short)ReadUInt16(_d, _deltaOff + 2 * i);
                    var rangeOffset = ReadUInt16(_d, _rangeOff + 2 * i);
                    if (rangeOffset == 0) return (c + delta) & 0xFFFF;

                    var p = _rangeOff + 2 * i + rangeOffset + 2 * (c - start);
                    if (p + 2 > _d.Length) return 0;
                    var g = ReadUInt16(_d, p);
                    return g == 0 ? 0 : (g + delta) & 0xFFFF;
                }
                return 0;
            }

            int Format12(char c)
            {
                // 分组按码点升序，二分
                int lo = 0, hi = (int)_nGroups - 1;
                while (lo <= hi)
                {
                    var mid = (lo + hi) / 2;
                    var o = _groupsOff + 12 * mid;
                    if (o + 12 > _d.Length) return 0;
                    var start = ReadUInt32(_d, o);
                    var end = ReadUInt32(_d, o + 4);
                    if (c < start) hi = mid - 1;
                    else if (c > end) lo = mid + 1;
                    else return (int)(ReadUInt32(_d, o + 8) + (c - start));
                }
                return 0;
            }
        }

        public struct Coverage
        {
            public int Covered;
            public string Missing;   // 源字体里查不到的字符
            public bool CmapUnreadable;
        }

        /// <summary>逐字符查源字体的 cmap，返回缺哪些。</summary>
        public static Coverage CheckCoverage(string fontAbsolutePath, string chars)
        {
            var result = new Coverage { Missing = "" };
            if (string.IsNullOrEmpty(chars)) return result;

            var cmap = CmapLookup.Load(fontAbsolutePath);
            if (cmap == null || !cmap.Valid)
            {
                result.CmapUnreadable = true;
                return result;
            }

            var missing = new System.Text.StringBuilder();
            foreach (var c in chars)
            {
                if (cmap.GetGlyphId(c) != 0) result.Covered++;
                else missing.Append(c);
            }
            result.Missing = missing.ToString();
            return result;
        }

        // ------------------------------------------------------------ 清理孤儿表

        public class CleanupReport
        {
            public List<string> Dropped = new List<string>();
            public bool ChecksumFixed;

            public string Describe()
            {
                var parts = new List<string>();
                if (Dropped.Count > 0) parts.Add("清理孤儿表 " + string.Join("/", Dropped));
                if (ChecksumFixed) parts.Add("修正 head.checkSumAdjustment");
                return parts.Count == 0 ? "" : string.Join("、", parts);
            }
        }

        /// <summary>
        /// 重建表目录与校验和，修掉 sfnttool 产物里会让系统字体校验报错的问题：
        /// - vhea：sfnttool 删了 vmtx 却留着 vhea，且 numberOfVMetrics 还是原字体的旧值
        /// - VORG：CFF 专用的垂直原点表，在 glyf 轮廓字体里是多余的
        /// - BASE：引用 GDEF 的脚本列表，而 GDEF 已被 sfnttool 删掉
        /// - head.checkSumAdjustment：sfnttool 写的整文件校验和一直是错的
        /// 无条件重建（即使没有孤儿表也要修校验和）。
        /// </summary>
        public static CleanupReport CleanOutput(string path)
        {
            var report = new CleanupReport();
            var d = File.ReadAllBytes(path);
            var entries = ReadTableDirectory(d);
            if (entries.Count == 0) return report;

            var tags = new HashSet<string>(entries.Select(e => e.Tag));
            var drop = new HashSet<string>();
            if (tags.Contains("vhea") && !tags.Contains("vmtx")) drop.Add("vhea");
            if (tags.Contains("VORG") && tags.Contains("glyf")) drop.Add("VORG");
            if (tags.Contains("BASE") && !tags.Contains("GDEF")) drop.Add("BASE");

            var oldHead = entries.FirstOrDefault(e => e.Tag == "head");
            var oldChecksumAdjustment = oldHead != null && oldHead.Offset + 12 <= d.Length
                ? ReadUInt32(d, (int)oldHead.Offset + 8)
                : 0u;

            var keep = entries.Where(e => !drop.Contains(e.Tag)).ToList();
            var n = keep.Count;

            // 数据按原偏移顺序摆放，目录按 tag 升序（sfnt 规范要求）
            var dataOrder = keep.OrderBy(e => e.Offset).ToList();
            var newOffset = new Dictionary<string, int>();
            var pos = 12 + 16 * n;
            foreach (var e in dataOrder)
            {
                newOffset[e.Tag] = pos;
                pos += (int)e.Length;
                pos = (pos + 3) & ~3; // 每张表 4 字节对齐
            }

            var o = new byte[pos];
            WriteUInt32(o, 0, ReadUInt32(d, 0)); // sfntVersion
            WriteUInt16(o, 4, (ushort)n);
            var pow2 = 1;
            var entrySelector = 0;
            while (pow2 * 2 <= n) { pow2 *= 2; entrySelector++; }
            WriteUInt16(o, 6, (ushort)(pow2 * 16));           // searchRange
            WriteUInt16(o, 8, (ushort)entrySelector);
            WriteUInt16(o, 10, (ushort)(n * 16 - pow2 * 16)); // rangeShift

            foreach (var e in dataOrder)
                Array.Copy(d, (int)e.Offset, o, newOffset[e.Tag], (int)e.Length);

            // head.checkSumAdjustment 必须先归零，再算各表和整文件校验和
            var headOffset = keep.Any(e => e.Tag == "head") ? newOffset["head"] : -1;
            if (headOffset >= 0) WriteUInt32(o, headOffset + 8, 0);

            var dir = keep.OrderBy(e => e.Tag, StringComparer.Ordinal).ToList();
            for (var i = 0; i < n; i++)
            {
                var e = dir[i];
                var eo = 12 + 16 * i;
                for (var k = 0; k < 4; k++) o[eo + k] = (byte)e.Tag[k];
                WriteUInt32(o, eo + 4, Checksum(o, newOffset[e.Tag], (int)e.Length));
                WriteUInt32(o, eo + 8, (uint)newOffset[e.Tag]);
                WriteUInt32(o, eo + 12, e.Length);
            }

            if (headOffset >= 0)
            {
                uint adjustment;
                unchecked { adjustment = 0xB1B0AFBAu - Checksum(o, 0, o.Length); }
                WriteUInt32(o, headOffset + 8, adjustment);
                report.ChecksumFixed = adjustment != oldChecksumAdjustment;
            }

            File.WriteAllBytes(path, o);
            report.Dropped = drop.OrderBy(x => x, StringComparer.Ordinal).ToList();
            return report;
        }

        static uint Checksum(byte[] d, int offset, int length)
        {
            uint sum = 0;
            var end = offset + length;
            for (var i = offset; i < end; i += 4)
            {
                uint v = 0;
                for (var k = 0; k < 4; k++)
                    v = (v << 8) | (i + k < end && i + k < d.Length ? d[i + k] : (byte)0);
                unchecked { sum += v; }
            }
            return sum;
        }

        static ushort ReadUInt16(byte[] d, int o) => (ushort)((d[o] << 8) | d[o + 1]);

        static uint ReadUInt32(byte[] d, int o) =>
            (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

        static void WriteUInt16(byte[] d, int o, ushort v)
        {
            d[o] = (byte)(v >> 8);
            d[o + 1] = (byte)v;
        }

        static void WriteUInt32(byte[] d, int o, uint v)
        {
            d[o] = (byte)(v >> 24);
            d[o + 1] = (byte)(v >> 16);
            d[o + 2] = (byte)(v >> 8);
            d[o + 3] = (byte)v;
        }
    }
}

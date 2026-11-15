// 태그(tags.csv)·계산 항목·읽기 반복·Run 기록

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RspLogger
{
    class Col
    {
        public string Key, Name, Unit;
        public bool Unconfirmed;   // 환산 배율·식이 현장 확인 전 임시값
    }

    class Tag : Col
    {
        public string Addr, Type, Note;   // Type: INT16 / INT32 / BIT
        public double Scale, Offset;
        public char Area;
        public int Word, Bit = -1;
    }

    class Block
    {
        public char Area;
        public int Start, Count;
    }

    static class Tags
    {
        public static List<Tag> Load(string path)
        {
            var rows = Csv.Read(path);
            if (rows.Count == 0) throw new FormatException("tags.csv 가 비었습니다.");
            var h = rows[0].Select((x, i) => new { x, i }).ToDictionary(p => p.x.Trim(), p => p.i);
            var tags = new List<Tag>();
            foreach (var r in rows.Skip(1))
            {
                Func<string, string> g = name =>
                {
                    int i;
                    if (!h.TryGetValue(name, out i)) throw new FormatException("tags.csv 에 '" + name + "' 열이 없습니다.");
                    return i < r.Length ? r[i] : "";
                };
                var t = new Tag
                {
                    Key = g("key"), Name = g("이름"), Addr = g("주소"), Type = g("타입").Trim().ToUpperInvariant(),
                    Unit = g("단위"), Unconfirmed = g("확인필요").Trim().ToUpperInvariant() == "Y", Note = g("비고"),
                };
                if (t.Type == "BIT") XgtProtocol.BitAddr(t.Addr, out t.Area, out t.Word, out t.Bit);
                else if (t.Type == "INT16" || t.Type == "INT32") XgtProtocol.WordAddr(t.Addr, out t.Area, out t.Word);
                else throw new FormatException(t.Key + ": 모르는 타입 " + t.Type);
                t.Scale = double.Parse(g("배율"), System.Globalization.NumberStyles.Float, App.Inv);
                t.Offset = double.Parse(g("오프셋"), System.Globalization.NumberStyles.Float, App.Inv);
                tags.Add(t);
            }
            return tags;
        }

        // 필요한 워드를 영역별로 묶어 요청 수를 줄인다
        public static List<Block> PlanBlocks(List<Tag> tags, int maxGap = 50, int maxWords = 700)
        {
            var need = new SortedDictionary<char, SortedSet<int>>();
            foreach (var t in tags)
            {
                SortedSet<int> s;
                if (!need.TryGetValue(t.Area, out s)) need[t.Area] = s = new SortedSet<int>();
                s.Add(t.Word);
                if (t.Type == "INT32") s.Add(t.Word + 1);
            }
            var blocks = new List<Block>();
            foreach (var kv in need)
            {
                int start = -1, prev = -1;
                foreach (int w in kv.Value)
                {
                    if (start < 0) start = prev = w;
                    else if (w - prev > maxGap || w - start + 1 > maxWords)
                    {
                        blocks.Add(new Block { Area = kv.Key, Start = start, Count = prev - start + 1 });
                        start = prev = w;
                    }
                    else prev = w;
                }
                blocks.Add(new Block { Area = kv.Key, Start = start, Count = prev - start + 1 });
            }
            return blocks;
        }

        // 원시 워드 -> 환산값·원시값
        public static void Decode(List<Tag> tags, List<Block> blocks, List<int[]> results,
            out Dictionary<string, double> values, out Dictionary<string, long> raws)
        {
            var mem = new Dictionary<long, int>();
            for (int b = 0; b < blocks.Count; b++)
                for (int i = 0; i < results[b].Length; i++)
                    mem[blocks[b].Area * 1000000L + blocks[b].Start + i] = results[b][i];
            values = new Dictionary<string, double>();
            raws = new Dictionary<string, long>();
            foreach (var t in tags)
            {
                int lo = mem[t.Area * 1000000L + t.Word];
                long raw;
                if (t.Type == "BIT")
                {
                    raw = (lo >> t.Bit) & 1;
                    values[t.Key] = raw;
                }
                else
                {
                    // TODO(확인필요): INT32 는 하위 워드가 앞 주소라고 가정 (LS 기본). 현장에서 XG5000 값과 대조
                    raw = t.Type == "INT32" ? (int)(((uint)mem[t.Area * 1000000L + t.Word + 1] << 16) | (uint)lo) : (short)lo;
                    values[t.Key] = raw * t.Scale + t.Offset;
                }
                raws[t.Key] = raw;
            }
        }

        // HMI 표시값과 PLC 원시값으로 배율 계산 (HMI = 원시값 × 배율 + 오프셋). 원시값 0이면 계산 불가 -> null
        public static double? SuggestScale(double raw, double hmi, double offset)
        {
            if (raw == 0) return null;
            return double.Parse(((hmi - offset) / raw).ToString("G6", App.Inv), App.Inv);
        }

        // tags.csv 저장. 먼저 임시 파일로 써서 Load 로 검사한 뒤 바꿔 끼운다 —
        // 잘못된 주소·타입·숫자가 있으면 예외를 내고 원래 파일은 그대로 둔다.
        public static void Save(string path, string[] header, List<string[]> rows)
        {
            string tmp = Path.ChangeExtension(path, ".tmp");
            using (var w = new StreamWriter(tmp, false, new UTF8Encoding(true)))
            {
                Csv.Row(w, header);
                foreach (var r in rows) Csv.Row(w, r);
            }
            try { Load(tmp); }
            catch (Exception e)
            {
                File.Delete(tmp);
                throw new FormatException("저장 안 함 — 잘못된 값: " + e.Message, e);
            }
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }
    }

    static class Calc
    {
        public static readonly List<Col> Derived = new List<Col>
        {
            new Col { Key = "wheel_v", Name = "휠 선속도", Unit = "m/s", Unconfirmed = true },
            new Col { Key = "dp", Name = "분사 차압 ΔP", Unit = "bar", Unconfirmed = true },
            new Col { Key = "vg1", Name = "진공도 VG1", Unit = "Torr", Unconfirmed = true },
            new Col { Key = "vg2", Name = "진공도 VG2", Unit = "Torr", Unconfirmed = true },
        };

        // 계산 항목을 values 에 더한다 (SPEC 5장). 휠 지름 0이면 선속도는 넣지 않는다(= 값 없음).
        public static void AddDerived(Dictionary<string, double> v, double wheelDmm)
        {
            double a, b;
            if (wheelDmm != 0 && v.TryGetValue("wheel_rpm", out a)) v["wheel_v"] = Math.PI * wheelDmm / 1000 * a / 60;
            if (v.TryGetValue("p_upper", out a) && v.TryGetValue("p_lower", out b)) v["dp"] = a - b;
            // TODO(확인필요): 진공도 표기(가수 소수 자릿수·지수 부호) 현장 확인 전 임시 식
            if (v.TryGetValue("vg1_mant", out a) && v.TryGetValue("vg1_exp", out b)) v["vg1"] = a * Math.Pow(10, b);
            if (v.TryGetValue("vg2_mant", out a) && v.TryGetValue("vg2_exp", out b)) v["vg2"] = a * Math.Pow(10, b);
        }
    }

    // 백그라운드 읽기 반복. 끊기면 1·2·5·10초 간격으로 재접속.
    class Poller
    {
        static readonly int[] Backoff = { 1, 2, 5, 10 };
        const double MinInterval = 0.1;   // CLAUDE.md 규칙 4
        readonly XgtClient client;
        readonly List<Tag> tags;
        public readonly List<Block> Blocks;
        public readonly double BaseInterval;
        double interval;
        readonly Action<double, Dictionary<string, double>, Dictionary<string, long>> onData;
        readonly Action<bool, string> onStatus;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        Thread th;

        public Poller(XgtClient client, List<Tag> tags, double interval,
            Action<double, Dictionary<string, double>, Dictionary<string, long>> onData, Action<bool, string> onStatus)
        {
            this.client = client; this.tags = tags; this.onData = onData; this.onStatus = onStatus;
            BaseInterval = this.interval = Math.Max(MinInterval, interval);
            Blocks = Tags.PlanBlocks(tags);
        }

        public void SetInterval(double s) { interval = Math.Max(MinInterval, s); }

        public void Start()
        {
            th = new Thread(Run) { IsBackground = true, Name = "poller" };
            th.Start();
        }

        public void Stop()
        {
            stop.Set();
            if (th != null) th.Join(5000);
        }

        void Run()
        {
            int fails = 0;
            var sw = Stopwatch.StartNew();
            while (!stop.WaitOne(0))
            {
                double t0 = sw.Elapsed.TotalSeconds;
                var results = new List<int[]>();
                try
                {
                    foreach (var b in Blocks) results.Add(client.ReadWords(b.Area, b.Start, b.Count));
                }
                catch (Exception e)
                {
                    client.Close();
                    int wait = Backoff[Math.Min(fails, Backoff.Length - 1)];
                    fails++;
                    onStatus(false, "끊김 (" + e.Message + ") — " + wait + "초 뒤 재접속");
                    stop.WaitOne(wait * 1000);
                    continue;
                }
                fails = 0;
                onStatus(true, "연결됨");
                Dictionary<string, double> values;
                Dictionary<string, long> raws;
                Tags.Decode(tags, Blocks, results, out values, out raws);
                onData(App.Now(), values, raws);
                double rest = interval - (sw.Elapsed.TotalSeconds - t0);
                if (rest > 0) stop.WaitOne((int)(rest * 1000));
            }
            client.Close();
        }
    }

    class Ejection
    {
        public int No;
        public double Start;
        public double? End;
        public string How;
        public List<Dictionary<string, double>> Samples = new List<Dictionary<string, double>>();
        public Dictionary<string, double> Before;
    }

    // Run 기록 — CSV(환산값)·_raw.csv(원시값)·run_index.csv (SPEC 6·8장).
    // Run 중에는 빠르게(0.2초) 읽은 값을 preS초 동안 붙잡아 두었다가 저장 여부를 정한다:
    // 출탕 구간 [시작−preS, 종료+postS] 안이면 모두, 밖이면 intervalS 간격으로만 저장.
    // 그래서 파일에는 최대 preS초 늦게 써진다 (정지·창 닫기 때는 남은 값을 모두 쓴다).
    class Recorder
    {
        public static readonly string[] MetaFields = { "시료명", "합금 조성", "투입 중량(g)", "노즐 슬릿", "노즐-휠 갭(mm)",
            "휠 재질", "휠 지름(mm)", "분위기 가스", "작업자", "메모" };
        public static readonly string[] ResultFields = { "리본 폭(mm)", "리본 두께(μm)", "외관", "비정질 여부" };
        // 출탕 요약에 넣는 항목 (시작값·평균·최대·최소)
        static readonly string[] SummaryKeys = { "wheel_v", "wheel_rpm", "dp", "p_upper", "p_lower", "t_melt", "t_nozzle",
            "t_wheel_out", "vg1", "vg2" };
        static readonly string[] StatNames = { "시작", "평균", "최대", "최소" };
        const string StartKey = "stopper_up";   // 0→1 상승 = 출탕 자동 시작 (DECISIONS 2026-11-14)

        class Pending
        {
            public double Ts;
            public Dictionary<string, double> Values;
            public Dictionary<string, long> Raws;
            public string Ev;
        }

        public readonly string OutDir;
        readonly List<Col> cols;
        readonly Dictionary<string, string> names;
        readonly List<string> rawKeys;
        readonly double intervalS, preS, postS;
        public string FilePath;
        public double T0;
        public int Rows;
        public List<Ejection> Ejections = new List<Ejection>();
        Dictionary<string, string> meta;
        Queue<Pending> pending;
        double? lastWritten, prevStartBit;
        Dictionary<string, double> prevSample;
        StreamWriter f, rf;

        public Recorder(string outDir, List<Tag> tags, double intervalS = 1.0, double preS = 10.0, double postS = 30.0)
        {
            OutDir = outDir;
            cols = tags.Cast<Col>().Concat(Calc.Derived).ToList();
            names = cols.ToDictionary(c => c.Key, c => c.Name);
            rawKeys = tags.Select(t => t.Key).ToList();
            this.intervalS = intervalS; this.preS = preS; this.postS = postS;
        }

        public bool Active { get { return FilePath != null; } }
        public bool Ejecting { get { return Ejections.Count > 0 && Ejections[Ejections.Count - 1].End == null; } }

        static string Num(double? v)
        {
            return v == null ? "" : v.Value.ToString("G6", App.Inv).Replace("E", "e");
        }

        static string Get(Dictionary<string, string> d, string k)
        {
            string v;
            return d != null && d.TryGetValue(k, out v) ? v ?? "" : "";
        }

        // ---------- Run ----------
        public string Start(double ts, Dictionary<string, string> meta)
        {
            Directory.CreateDirectory(OutDir);
            string name = Regex.Replace(Get(meta, "시료명").Trim(), @"[\\/:*?""<>|\s]+", "_");
            if (name == "") name = "무제";
            string bas = App.Local(ts).ToString("yyyyMMdd_HHmm", App.Inv) + "_" + name;
            Func<int, string> P = k => Path.Combine(OutDir, bas + (k == 1 ? "" : "_" + k) + ".csv");
            int n = 1;
            while (File.Exists(P(n))) n++;
            string path = P(n);
            this.meta = new Dictionary<string, string>(meta);
            T0 = ts; Rows = 0;
            pending = new Queue<Pending>();
            lastWritten = null; prevStartBit = null; prevSample = null;
            Ejections = new List<Ejection>();
            f = new StreamWriter(path, false, new UTF8Encoding(true));
            rf = new StreamWriter(Path.Combine(OutDir, Path.GetFileNameWithoutExtension(path) + "_raw.csv"), false, new UTF8Encoding(true));
            FilePath = path;
            foreach (var w in new[] { f, rf })
            {
                Csv.Row(w, new[] { "# 시작", App.Stamp(ts) });
                foreach (var k in MetaFields) Csv.Row(w, new[] { "# " + k, Get(meta, k) });
            }
            Csv.Row(f, new[] { "시각", "경과초" }.Concat(cols.Select(c => c.Unit != "" && c.Unit != "-" ? c.Name + "(" + c.Unit + ")" : c.Name))
                .Concat(new[] { "출탕구간번호", "이벤트" }));
            Csv.Row(rf, new[] { "시각", "경과초" }.Concat(rawKeys).Concat(new[] { "이벤트" }));
            Event(ts, "Run 시작");
            return path;
        }

        public void Add(double ts, Dictionary<string, double> values, Dictionary<string, long> raws)
        {
            if (!Active) return;
            double bit;
            bool has = values.TryGetValue(StartKey, out bit);
            if (has && prevStartBit == 0 && bit == 1) EjectStart(ts, "자동(M151)");
            if (has) prevStartBit = bit;
            if (Ejecting) Ejections[Ejections.Count - 1].Samples.Add(values);
            else prevSample = values;
            pending.Enqueue(new Pending { Ts = ts, Values = values, Raws = raws, Ev = "" });
            Drain(ts - preS);
        }

        // 값 없이 이벤트만 남기는 행 (Run 시작·정지, 연결 끊김, 출탕 시작·종료)
        public void Event(double ts, string text)
        {
            if (Active)
                pending.Enqueue(new Pending { Ts = ts, Values = new Dictionary<string, double>(), Raws = new Dictionary<string, long>(), Ev = text });
        }

        // ---------- 출탕 ----------
        public int? EjectStart(double ts, string how = "수동")
        {
            if (!Active || Ejecting) return null;
            int no = Ejections.Count + 1;
            Ejections.Add(new Ejection { No = no, Start = ts, How = how, Before = prevSample });
            Event(ts, "출탕 " + no + " 시작(" + how + ")");
            return no;
        }

        public int? EjectEnd(double ts, string why = "")
        {
            if (!Ejecting) return null;
            var e = Ejections[Ejections.Count - 1];
            e.End = ts;
            Event(ts, "출탕 " + e.No + " 종료" + why);
            return e.No;
        }

        string EjectNo(double t)
        {
            foreach (var e in Ejections)
                if (e.Start <= t && (e.End == null || t <= e.End)) return e.No.ToString(App.Inv);
            return "";
        }

        bool InWindow(double t)
        {
            return Ejections.Any(e => e.Start - preS <= t && (e.End == null || t <= e.End + postS));
        }

        // upto 이전 값들의 저장 여부를 정해 파일에 쓴다
        void Drain(double upto)
        {
            while (pending.Count > 0 && pending.Peek().Ts <= upto)
            {
                var p = pending.Dequeue();
                if (p.Ev != "" || InWindow(p.Ts) || lastWritten == null || p.Ts - lastWritten >= intervalS * 0.95)
                    Write(p);
            }
            f.Flush();
            rf.Flush();
        }

        void Write(Pending p)
        {
            var head = new[] { App.Stamp(p.Ts), (p.Ts - T0).ToString("F3", App.Inv) };
            string no = p.Ev != "" ? "" : EjectNo(p.Ts);
            Csv.Row(f, head.Concat(cols.Select(c => { double v; return p.Values.TryGetValue(c.Key, out v) ? Num(v) : ""; }))
                .Concat(new[] { no, p.Ev }));
            Csv.Row(rf, head.Concat(rawKeys.Select(k => { long v; return p.Raws.TryGetValue(k, out v) ? v.ToString(App.Inv) : ""; }))
                .Concat(new[] { p.Ev }));
            if (p.Ev == "")
            {
                lastWritten = p.Ts;
                Rows++;
            }
        }

        // ---------- 요약 ----------
        public List<KeyValuePair<string, string>> Summary(Ejection e)
        {
            var o = new List<KeyValuePair<string, string>>();
            Action<string, string> put = (k, v) => o.Add(new KeyValuePair<string, string>(k, v));
            put("출탕번호", e.No.ToString(App.Inv));
            put("출탕 시작", App.Stamp(e.Start));
            put("출탕 종료", App.Stamp(e.End.Value));
            put("출탕 시간(s)", (e.End.Value - e.Start).ToString("F1", App.Inv));
            put("판정", e.How);
            foreach (var k in SummaryKeys)
            {
                var vals = e.Samples.Where(x => x.ContainsKey(k)).Select(x => x[k]).ToList();
                var stats = vals.Count > 0
                    ? new double?[] { vals[0], vals.Average(), vals.Max(), vals.Min() }
                    : new double?[] { null, null, null, null };
                for (int i = 0; i < 4; i++) put(ColName(k) + " " + StatNames[i], Num(stats[i]));
            }
            var temps = e.Samples.Where(x => x.ContainsKey("t_wheel_out")).Select(x => x["t_wheel_out"]).ToList();
            double before;
            bool hasBefore = e.Before != null && e.Before.TryGetValue("t_wheel_out", out before);
            put("휠 출수온도 상승폭", temps.Count > 0 && hasBefore ? Num(temps.Max() - e.Before["t_wheel_out"]) : "");
            return o;
        }

        string ColName(string k)
        {
            string n;
            return names.TryGetValue(k, out n) ? n : k;
        }

        public List<string> SummaryColumns()
        {
            var c = new List<string> { "출탕번호", "출탕 시작", "출탕 종료", "출탕 시간(s)", "판정" };
            foreach (var k in SummaryKeys)
                foreach (var s in StatNames) c.Add(ColName(k) + " " + s);
            c.Add("휠 출수온도 상승폭");
            return c;
        }

        public string Stop(double ts, Dictionary<string, string> results = null)
        {
            if (!Active) return null;
            results = results ?? new Dictionary<string, string>();
            EjectEnd(ts, "(Run 정지)");
            Event(ts, "Run 정지");
            Drain(double.PositiveInfinity);
            var sums = Ejections.Select(Summary).ToList();
            foreach (var w in new[] { f, rf })
            {
                Csv.Row(w, new[] { "# 종료", App.Stamp(ts) });
                foreach (var k in ResultFields) Csv.Row(w, new[] { "# " + k, Get(results, k) });
            }
            if (sums.Count > 0)
            {
                Csv.Row(f, new[] { "# 출탕 요약" }.Concat(sums[0].Select(x => x.Key)));
                foreach (var row in sums) Csv.Row(f, new[] { "#" }.Concat(row.Select(x => x.Value)));
            }
            f.Close();
            rf.Close();
            WriteIndex(ts, results, sums);
            string path = FilePath;
            FilePath = null;
            return path;
        }

        // run_index.csv — 출탕마다 한 줄 (출탕이 없으면 Run 한 줄)
        void WriteIndex(double ts, Dictionary<string, string> results, List<List<KeyValuePair<string, string>>> sums)
        {
            var sumCols = SummaryColumns();
            var header = new[] { "파일", "Run 시작", "Run 종료", "기록초", "행수" }.Concat(MetaFields).Concat(ResultFields).Concat(sumCols).ToArray();
            string index = Path.Combine(OutDir, "run_index.csv");
            if (File.Exists(index))
            {
                var old = Csv.Read(index).FirstOrDefault();
                if (old == null || !old.SequenceEqual(header))   // 열 구성이 바뀌면 옛 파일은 이름을 바꿔 보존
                    File.Move(index, Path.Combine(OutDir, "run_index_이전_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", App.Inv) + ".csv"));
            }
            bool isNew = !File.Exists(index);
            var bas = new[] { Path.GetFileName(FilePath), App.Stamp(T0), App.Stamp(ts), (ts - T0).ToString("F1", App.Inv), Rows.ToString(App.Inv) }
                .Concat(MetaFields.Select(k => Get(meta, k))).Concat(ResultFields.Select(k => Get(results, k))).ToList();
            using (var w = new StreamWriter(index, true, new UTF8Encoding(isNew)))
            {
                if (isNew) Csv.Row(w, header);
                if (sums.Count == 0) Csv.Row(w, bas.Concat(sumCols.Select(c => "")));
                foreach (var s in sums)
                {
                    var d = s.ToDictionary(x => x.Key, x => x.Value);
                    Csv.Row(w, bas.Concat(sumCols.Select(c => Get(d, c))));
                }
            }
        }
    }
}

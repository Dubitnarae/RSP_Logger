// HMI 원본(M2I TOP Design Studio .TDS) 메인화면 해독·그리기, 기록 다시보기.
// 원본 파일은 회사 자료라 저장소에 넣지 않는다 — 실행할 때 사용자가 지정한 파일을 읽어서 그린다.
// 형식은 직접 해독한 것 (2026-11-24, docs/hmi/tdsparse.py 시제품과 같은 규칙):
//   "HD_xxx" 구획마다 +130 바이트부터 raw deflate. HD_SCR_DAT 중 가장 큰 것 = 메인화면(1024×768).
//   앞쪽은 그림 묶음(BMP, 이름 문자열 40바이트 앞에 그림 번호), 뒤쪽 부품은 [순번 2바이트] CD AB 로 시작.
//   부품 종류 = 표시 8바이트 앞 4바이트, 위치 l,t,r,b = 부품+96, 색은 R,G,B 바이트 순서.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace RspLogger
{
    class HmiText
    {
        public string Text;
        public Color Color;
        public int Size;
        public bool Bold;
    }

    class HmiState
    {
        public Color C1, C2, C3;
        public int Image = -1;
    }

    class HmiObj
    {
        public string Type;   // rect line text number switch clock (그 밖은 그리지 않음)
        public int L, T, R, B;
        public Color Line, Fill;
        public bool Border;
        public List<HmiText> Texts = new List<HmiText>();
        public string Addr;   // 스위치·램프 = 상태 비트, 숫자 = 워드. 원본 표기 그대로 (예: M00118, D000401)
        public List<HmiState> States = new List<HmiState>();
        public int Decimals;  // 숫자 표시 소수 자릿수 (원본 예시 값 "12.3" 에서)
    }

    class HmiScreen
    {
        public const int W = 1024, H = 768;
        public List<HmiObj> Objects = new List<HmiObj>();
        public Dictionary<int, Bitmap> Images = new Dictionary<int, Bitmap>();

        static readonly Dictionary<string, string> Types = new Dictionary<string, string>
        {
            { "01030000", "rect" }, { "01020000", "line" }, { "01040000", "text" }, { "02050100", "number" },
            { "01010100", "switch" }, { "01050100", "clock" },
        };

        static int I32(byte[] s, int p) { return BitConverter.ToInt32(s, p); }
        static Color Rgb(byte[] s, int p) { return Color.FromArgb(s[p], s[p + 1], s[p + 2]); }

        static int IndexOf(byte[] d, byte[] pat, int from, int to)
        {
            for (int i = Math.Max(0, from); i <= Math.Min(d.Length, to) - pat.Length; i++)
            {
                int k = 0;
                while (k < pat.Length && d[i + k] == pat[k]) k++;
                if (k == pat.Length) return i;
            }
            return -1;
        }

        static byte[] Inflate(byte[] d, int at)
        {
            using (var ms = new MemoryStream(d, at, d.Length - at))
            using (var z = new DeflateStream(ms, CompressionMode.Decompress))
            using (var o = new MemoryStream())
            {
                z.CopyTo(o);
                return o.ToArray();
            }
        }

        // 메인화면(가장 큰 HD_SCR_DAT) 압축 풀기
        public static byte[] MainScreenBytes(byte[] tds)
        {
            var name = Encoding.ASCII.GetBytes("HD_SCR_DAT");
            byte[] best = null;
            for (int p = IndexOf(tds, name, 0, tds.Length); p >= 0; p = IndexOf(tds, name, p + 1, tds.Length))
            {
                byte[] z;
                try { z = Inflate(tds, p + 130); }
                catch (InvalidDataException) { continue; }
                if (best == null || z.Length > best.Length) best = z;
            }
            if (best == null) throw new FormatException("HMI 화면 데이터를 찾지 못했습니다 (M2I TOP Design Studio .TDS 파일인지 확인).");
            return best;
        }

        public static HmiScreen Load(string path)
        {
            var z = MainScreenBytes(File.ReadAllBytes(path));
            var scr = new HmiScreen();
            var marks = new List<int>();
            for (int i = 2; i < z.Length - 1; i++)
                if (z[i] == 0xCD && z[i + 1] == 0xAB) marks.Add(i - 2);
            if (marks.Count == 0) throw new FormatException("HMI 화면에 부품이 없습니다.");
            scr.LoadImages(z, marks[0]);
            marks.Add(z.Length);
            for (int k = 0; k + 1 < marks.Count; k++)
            {
                int a = marks[k], b = marks[k + 1];
                if (a < 8 || b - a < 200) continue;
                string typ;
                if (!Types.TryGetValue(BitConverter.ToString(z, a - 8, 4).Replace("-", "").ToLowerInvariant(), out typ)) continue;
                var s = new byte[b - a];
                Array.Copy(z, a, s, 0, s.Length);
                var o = new HmiObj
                {
                    Type = typ, L = I32(s, 96), T = I32(s, 100), R = I32(s, 104), B = I32(s, 108),
                    Line = Rgb(s, 191), Fill = Rgb(s, 195), Border = s[187] == 1,
                };
                o.Texts = Texts(s);
                var adr = Addrs(s);
                o.Addr = adr.Count > 0 ? adr[0] : null;   // 스위치: 첫 주소 = 램프(상태), 둘째 = 터치(쓰기) — 쓰기는 안 쓴다
                if (typ == "switch") o.States = States(s, scr.Images);
                if (typ == "number" && o.Texts.Count > 0)
                {
                    string sample = o.Texts[o.Texts.Count - 1].Text;
                    int dot = sample.IndexOf('.');
                    o.Decimals = dot < 0 ? 0 : sample.Length - dot - 1;
                }
                scr.Objects.Add(o);
            }
            return scr;
        }

        // 그림 묶음: "...이름.bmp|.lpk" 문자열 40바이트 앞이 그림 번호, 뒤에 BMP
        void LoadImages(byte[] z, int end)
        {
            for (int o = 0; o + 54 < end; o++)
            {
                if (z[o] != 'B' || z[o + 1] != 'M') continue;
                int size = I32(z, o + 2), res = I32(z, o + 6), off = I32(z, o + 10), hs = I32(z, o + 14);
                if (res != 0 || hs != 40 || off < 54 || off > 2000 || size < 100 || size > 5000000 || o + size > z.Length) continue;
                int dot = -1;
                foreach (var ext in new[] { ".bmp", ".lpk" })
                    dot = Math.Max(dot, LastIndexOf(z, Encoding.Unicode.GetBytes(ext), Math.Max(0, o - 600), o));
                if (dot < 0) continue;
                int p = dot;
                while (p - 2 >= 0 && z[p - 1] == 0 && z[p - 2] >= 0x20 && z[p - 2] < 0x7F) p -= 2;
                if (p < 40) continue;
                int id = I32(z, p - 40);
                try
                {
                    using (var ms = new MemoryStream(z, o, size))
                    using (var bmp = new Bitmap(ms))
                    {
                        var copy = new Bitmap(bmp);
                        copy.MakeTransparent(Color.Black);   // 라이브러리 그림 배경이 검정
                        Images[id] = copy;
                    }
                }
                catch (ArgumentException) { }
                o += size - 1;
            }
        }

        static int LastIndexOf(byte[] d, byte[] pat, int from, int to)
        {
            for (int i = to - pat.Length; i >= from; i--)
            {
                int k = 0;
                while (k < pat.Length && d[i + k] == pat[k]) k++;
                if (k == pat.Length) return i;
            }
            return -1;
        }

        // 길이(4)+UTF16+NUL 문자열 뒤에 88 00 00 00 + 글자색(4) + 배경(4) + LOGFONTW(92)
        static List<HmiText> Texts(byte[] s)
        {
            var r = new List<HmiText>();
            for (int p = 4; p + 12 + 92 <= s.Length; p++)
            {
                if (s[p] != 0x88 || s[p + 1] != 0 || s[p + 2] != 0 || s[p + 3] != 0 || s[p - 1] != 0 || s[p - 2] != 0) continue;
                int lf = p + 12;
                char f0 = (char)(s[lf + 28] | (s[lf + 29] << 8));
                if (!char.IsLetter(f0)) continue;
                for (int len = 2; len < 600 && p - len - 4 >= 0; len += 2)
                {
                    if (I32(s, p - len - 4) != len) continue;
                    r.Add(new HmiText
                    {
                        Text = Encoding.Unicode.GetString(s, p - len, len - 2), Color = Rgb(s, p + 4),
                        Size = Math.Abs(I32(s, lf)), Bold = I32(s, lf + 16) >= 600,
                    });
                    break;
                }
            }
            return r;
        }

        // 길이(4) + "M00118" 같은 주소 UTF16 + NUL
        static List<string> Addrs(byte[] s)
        {
            var r = new List<string>();
            for (int p = 4; p + 2 < s.Length; p += 1)
            {
                if ("PMKLFDTCNRUZ".IndexOf((char)s[p]) < 0 || s[p + 1] != 0) continue;
                int n = 1;
                while (p + 2 * n + 1 < s.Length && s[p + 2 * n + 1] == 0 && Uri.IsHexDigit((char)s[p + 2 * n])) n++;
                if (n < 4 || n > 7 || p + 2 * n + 1 >= s.Length || s[p + 2 * n] != 0 || s[p + 2 * n + 1] != 0) continue;
                if (I32(s, p - 4) != (n + 1) * 2) continue;
                r.Add(Encoding.Unicode.GetString(s, p, n * 2));
            }
            return r;
        }

        // 상태 문자열 "[10:1000N]=..." : 끝 +52 = 그림 번호, 시작 -109/-105/-101 = 색 3개
        static List<HmiState> States(byte[] s, Dictionary<int, Bitmap> images)
        {
            var r = new List<HmiState>();
            var pat = Encoding.Unicode.GetBytes("[10:1000");
            for (int p = IndexOf(s, pat, 0, s.Length); p >= 0; p = IndexOf(s, pat, p + 1, s.Length))
            {
                if (p < 109) continue;
                int len = I32(s, p - 4);
                if (len < 4 || p + len > s.Length) continue;
                int end = p + len - 2;
                int img = end + 56 <= s.Length ? I32(s, end + 52) : -1;
                r.Add(new HmiState { C1 = Rgb(s, p - 109), C2 = Rgb(s, p - 105), C3 = Rgb(s, p - 101), Image = images.ContainsKey(img) ? img : -1 });
            }
            return r;
        }

        // ---------- 주소 ----------
        // 원본 주소 -> 기록 키("@M11", "@D401")와 비트 번호(-1 = 워드). T·C 등 다른 영역은 null (PLC가 거부할 수 있어 읽지 않는다)
        public static string KeyOf(string addr, out int bit)
        {
            bit = -1;
            if (string.IsNullOrEmpty(addr)) return null;
            char a = addr[0];
            int w;
            try
            {
                if ("PMK".IndexOf(a) >= 0) { char ar; XgtProtocol.BitAddr(addr, out ar, out w, out bit); }
                else if (a == 'D') { char ar; XgtProtocol.WordAddr(addr, out ar, out w); }
                else return null;
            }
            catch (FormatException) { return null; }
            return "@" + a + w.ToString(CultureInfo.InvariantCulture);
        }

        // 기록할 워드 키 목록. int32 = tags.csv 에서 INT32 인 주소 (다음 워드도 같이)
        public List<string> WordKeys(ICollection<string> int32)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var o in Objects)
            {
                int bit;
                string k = KeyOf(o.Addr, out bit);
                if (k == null) continue;
                keys.Add(k);
                if (bit < 0 && int32.Contains(k)) keys.Add(NextKey(k));
            }
            return keys.ToList();
        }

        public static string NextKey(string k) { return k.Substring(0, 2) + (int.Parse(k.Substring(2), CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture); }

        public static string FormatNumber(long raw, int decimals)
        {
            return (raw / Math.Pow(10, decimals)).ToString("F" + decimals, CultureInfo.InvariantCulture);
        }
    }

    // 화면 그리기 — word(key) 는 기록된 원시 워드(없으면 null)
    class HmiPainter
    {
        readonly HmiScreen scr;
        readonly HashSet<string> int32;
        readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        static readonly string Family = new[] { "Pretendard", "Noto Sans KR", "맑은 고딕" }
            .FirstOrDefault(n => FontFamily.Families.Any(f => f.Name == n)) ?? "맑은 고딕";

        public HmiPainter(HmiScreen scr, ICollection<string> int32Keys)
        {
            this.scr = scr;
            int32 = new HashSet<string>(int32Keys);
        }

        Font F(int px, bool bold)
        {
            string k = px + (bold ? "b" : "");
            Font f;
            if (!fonts.TryGetValue(k, out f)) fonts[k] = f = new Font(Family, Math.Max(7, px * 0.85f), bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            return f;
        }

        static void Center(Graphics g, string text, Font f, Color c, RectangleF r)
        {
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoClip | StringFormatFlags.NoWrap, Trimming = StringTrimming.None })
                g.DrawString(text.Replace("\r", ""), f, b, r, sf);
        }

        public void Paint(Graphics g, Func<string, long?> word, string clock)
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            foreach (var o in scr.Objects)
            {
                var r = new RectangleF(o.L, o.T, o.R - o.L, o.B - o.T);
                if (o.Type == "line")
                {
                    using (var p = new Pen(o.Fill)) g.DrawLine(p, o.L, o.T, o.R, o.B);
                    continue;
                }
                if (r.Width < 0 || r.Height < 0 || o.R > HmiScreen.W + 50 || o.B > HmiScreen.H + 50) continue;
                switch (o.Type)
                {
                    case "rect":
                        using (var b = new SolidBrush(o.Fill)) g.FillRectangle(b, r);
                        if (o.Border) using (var p = new Pen(o.Line)) g.DrawRectangle(p, o.L, o.T, r.Width, r.Height);
                        break;
                    case "text":
                        if (o.Texts.Count > 0) Center(g, o.Texts[0].Text, F(o.Texts[0].Size, o.Texts[0].Bold), o.Texts[0].Color, r);
                        break;
                    case "number":
                        {
                            g.FillRectangle(Brushes.Black, r);
                            g.DrawRectangle(Pens.Gray, o.L, o.T, r.Width, r.Height);
                            int bit;
                            string k = HmiScreen.KeyOf(o.Addr, out bit);
                            long? v = k == null ? null : word(k);
                            if (v != null && int32.Contains(k))
                            {
                                long? hi = word(HmiScreen.NextKey(k));
                                v = hi == null ? (long?)null : (int)(((uint)hi.Value << 16) | ((uint)v.Value & 0xFFFF));
                            }
                            else if (v != null) v = (short)v.Value;
                            var t = o.Texts.Count > 0 ? o.Texts[o.Texts.Count - 1] : new HmiText { Size = 14 };
                            Center(g, v == null ? "—" : HmiScreen.FormatNumber(v.Value, o.Decimals), F(t.Size, t.Bold), Color.Lime, r);
                            break;
                        }
                    case "switch":
                        {
                            int bit;
                            string k = HmiScreen.KeyOf(o.Addr, out bit);
                            long? w = k == null ? null : word(k);
                            bool on = w != null && bit >= 0 && ((w.Value >> bit) & 1) == 1;
                            int si = o.States.Count >= 2 ? o.States.Count - 2 + (on ? 1 : 0) : -1;   // 끝 두 상태 = 꺼짐·켜짐
                            if (si >= 0)
                            {
                                var st = o.States[si];
                                Bitmap img;
                                if (st.Image >= 0 && scr.Images.TryGetValue(st.Image, out img)) g.DrawImage(img, r);
                                else
                                {
                                    using (var b = new SolidBrush(st.C2)) g.FillRectangle(b, r);
                                    using (var p = new Pen(st.C3)) g.DrawRectangle(p, o.L, o.T, r.Width, r.Height);
                                }
                            }
                            if (o.Texts.Count > 0)
                            {
                                var t = o.Texts.Count == o.States.Count && si >= 0 ? o.Texts[si] : o.Texts[0];
                                Center(g, t.Text, F(t.Size, t.Bold), t.Color, r);
                            }
                            break;
                        }
                    case "clock":
                        g.DrawRectangle(Pens.Gray, o.L, o.T, r.Width, r.Height);
                        Center(g, clock, F(16, false), Color.Black, r);
                        break;
                }
            }
        }
    }

    // 기록 다시보기 — Run CSV 옆 _hmi.csv 를 시간 막대로 재생
    class HmiReplayForm : Form
    {
        class Canvas : Panel
        {
            public Canvas() { DoubleBuffered = true; ResizeRedraw = true; BackColor = Color.DimGray; }
        }

        readonly HmiPainter painter;
        readonly List<double> secs = new List<double>();
        readonly List<string> stamps = new List<string>();
        readonly List<long?[]> rows = new List<long?[]>();
        readonly Dictionary<string, int> col = new Dictionary<string, int>();
        readonly Canvas canvas = new Canvas { Dock = DockStyle.Fill };
        readonly TrackBar bar = new TrackBar { Dock = DockStyle.Fill, TickStyle = TickStyle.None };
        readonly Label timeLbl = new Label { AutoSize = false, Width = 230, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Right };
        readonly Button play = new Button { Text = "▶ 재생", Width = 80, Dock = DockStyle.Left };
        readonly ComboBox speed = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70, Dock = DockStyle.Left };
        readonly Timer timer = new Timer { Interval = 100 };
        double pos;   // 지금 보는 경과초
        int idx;

        public static string HmiCsvPath(string runCsv)
        {
            return Path.Combine(Path.GetDirectoryName(runCsv), Path.GetFileNameWithoutExtension(runCsv) + "_hmi.csv");
        }

        // _hmi.csv 읽기: 시각, 경과초, @키...
        public static void LoadHmiCsv(string path, List<string> stamps, List<double> secs, List<long?[]> rows, Dictionary<string, int> col)
        {
            var all = Csv.Read(path);
            if (all.Count == 0 || all[0].Length < 2 || all[0][1] != "경과초") throw new FormatException("HMI 기록 파일 형식이 아닙니다: " + path);
            for (int i = 2; i < all[0].Length; i++) col[all[0][i]] = i - 2;
            foreach (var r in all.Skip(1))
            {
                double t;
                if (r.Length < 2 || !double.TryParse(r[1], NumberStyles.Float, CultureInfo.InvariantCulture, out t)) continue;
                var v = new long?[col.Count];
                for (int i = 0; i < v.Length && i + 2 < r.Length; i++)
                {
                    long x;
                    if (long.TryParse(r[i + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)) v[i] = x;
                }
                stamps.Add(r[0]); secs.Add(t); rows.Add(v);
            }
            if (rows.Count == 0) throw new FormatException("HMI 기록에 값이 없습니다: " + path);
        }

        // 파일 고르기 → 해독 시험 → settings.toml [hmi] file 저장. 실패·취소면 null
        public static string ChooseTds(IWin32Window owner)
        {
            using (var d = new OpenFileDialog { Title = "HMI 원본 파일 (M2I TOP Design Studio)", Filter = "TOP Design Studio (*.tds)|*.tds|모든 파일|*.*" })
            {
                if (d.ShowDialog(owner) != DialogResult.OK) return null;
                try
                {
                    if (d.FileName.Contains("'")) throw new FormatException("경로에 작은따옴표(')가 있으면 쓸 수 없습니다.");
                    var scr = HmiScreen.Load(d.FileName);
                    string s = File.ReadAllText(App.SettingsPath, Encoding.UTF8);
                    s = Settings.Upsert(s, "hmi", "file", "'" + d.FileName + "'");
                    Settings.Parse(s.TrimStart('\uFEFF'));
                    File.WriteAllText(App.SettingsPath, s, new UTF8Encoding(false));
                    MessageBox.Show(owner, "HMI 메인화면을 읽었습니다 (부품 " + scr.Objects.Count + "개, 그림 " + scr.Images.Count + "개).", App.Name);
                    return d.FileName;
                }
                catch (Exception e)
                {
                    MessageBox.Show(owner, "HMI 원본 파일을 쓸 수 없습니다:\n" + e.Message, App.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }
            }
        }

        // 지난 기록 창의 "HMI 화면으로 다시보기"
        public static void Open(IWin32Window owner, string runCsv)
        {
            try
            {
                if (!File.Exists(HmiCsvPath(runCsv))) throw new FileNotFoundException("이 Run 에는 HMI 화면 기록(_hmi.csv)이 없습니다. HMI 원본 파일을 지정한 뒤 기록한 Run 만 다시볼 수 있습니다.");
                var cfg = Settings.Parse(File.ReadAllText(App.SettingsPath, Encoding.UTF8).TrimStart('\uFEFF'));
                string tds = cfg.StrOr("hmi.file", "");
                if (tds == "" || !File.Exists(tds)) tds = ChooseTds(owner);
                if (tds == null) return;
                var tags = Tags.Load(Path.Combine(App.ConfigDir, "tags.csv"));
                new HmiReplayForm(runCsv, HmiScreen.Load(tds), App.Int32Keys(tags)).Show(owner);
            }
            catch (Exception e)
            {
                MessageBox.Show(owner, e.Message, "HMI 다시보기", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public HmiReplayForm(string runCsv, HmiScreen scr, ICollection<string> int32Keys)
        {
            LoadHmiCsv(HmiCsvPath(runCsv), stamps, secs, rows, col);
            painter = new HmiPainter(scr, int32Keys);
            Text = "HMI 다시보기 — " + Path.GetFileName(runCsv);
            Font = Ui.Font;
            Size = new Size(1100, 900);
            foreach (var s in new[] { "×1", "×2", "×5", "×10", "×30" }) speed.Items.Add(s);
            speed.SelectedIndex = 0;
            bar.Maximum = rows.Count - 1;
            bar.ValueChanged += (s, e) => { idx = bar.Value; pos = secs[idx]; Refresh2(); };
            play.Click += (s, e) => { timer.Enabled = !timer.Enabled; play.Text = timer.Enabled ? "❚❚ 멈춤" : "▶ 재생"; };
            timer.Tick += (s, e) => Step();
            canvas.Paint += (s, e) => PaintCanvas(e.Graphics);
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6) };
            bottom.Controls.Add(bar);
            bottom.Controls.Add(timeLbl);
            bottom.Controls.Add(speed);
            bottom.Controls.Add(play);
            Controls.Add(canvas);
            Controls.Add(bottom);
            FormClosed += (s, e) => timer.Dispose();
            Refresh2();
        }

        void Step()
        {
            double sp = double.Parse(((string)speed.SelectedItem).Substring(1), CultureInfo.InvariantCulture);
            pos += timer.Interval / 1000.0 * sp;
            int i = idx;
            while (i + 1 < secs.Count && secs[i + 1] <= pos) i++;
            if (i >= secs.Count - 1) { timer.Enabled = false; play.Text = "▶ 재생"; }
            if (i != idx) bar.Value = i;   // ValueChanged 가 다시 그린다
        }

        void Refresh2()
        {
            timeLbl.Text = "  " + stamps[idx] + "  (" + secs[idx].ToString("F1", CultureInfo.InvariantCulture) + "초, " + (idx + 1) + "/" + rows.Count + ")";
            canvas.Invalidate();
        }

        void PaintCanvas(Graphics g)
        {
            float k = Math.Min(canvas.ClientSize.Width / (float)HmiScreen.W, canvas.ClientSize.Height / (float)HmiScreen.H);
            if (k <= 0) return;
            g.TranslateTransform((canvas.ClientSize.Width - HmiScreen.W * k) / 2, (canvas.ClientSize.Height - HmiScreen.H * k) / 2);
            g.ScaleTransform(k, k);
            g.SetClip(new Rectangle(0, 0, HmiScreen.W, HmiScreen.H));
            var row = rows[idx];
            painter.Paint(g, key => { int i; return col.TryGetValue(key, out i) ? row[i] : null; }, stamps[idx].Length >= 19 ? stamps[idx].Substring(2, 17) : stamps[idx]);
        }
    }
}

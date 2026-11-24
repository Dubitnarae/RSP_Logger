// 지난 기록 보기 · 태그 설정/환산 확인 화면

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace RspLogger
{
    // 지난 기록 보기 — 저장한 Run CSV를 열어 그래프로 다시 본다 (출탕 구간 음영·이벤트 선 포함)
    class HistoryForm : Form
    {
        static readonly HashSet<string> Skip = new HashSet<string> { "시각", "경과초", "출탕구간번호", "이벤트" };
        static readonly string[] Default = { "상부 챔버 압력", "하부 챔버 압력", "분사 차압" };

        readonly Chart chart = Ui.NewChart();
        readonly CheckedListBox list = new CheckedListBox { Dock = DockStyle.Left, Width = 300, CheckOnClick = true, IntegralHeight = false };
        readonly List<double> x;
        readonly Dictionary<string, List<double>> cols = new Dictionary<string, List<double>>();
        readonly Dictionary<string, Series> curves = new Dictionary<string, Series>();

        // -> 메타 [(키, 값)], 열 이름 목록, 행 목록
        public static void LoadRun(string path, out List<KeyValuePair<string, string>> meta, out string[] hdr,
            out List<Dictionary<string, string>> data)
        {
            var rows = Csv.Read(path);
            int hi = rows.FindIndex(r => r.Length > 0 && r[0] == "시각");
            if (hi < 0) throw new FormatException("RSP Logger 기록 파일이 아닙니다 ('시각' 머리줄 없음).");
            meta = rows.Where(r => r.Length == 2 && r[0].StartsWith("#") && r[1] != "")
                .Select(r => new KeyValuePair<string, string>(r[0].Substring(1).Trim(), r[1])).ToList();
            var h = hdr = rows[hi];
            data = rows.Skip(hi + 1).Where(r => r.Length > 0 && !r[0].StartsWith("#"))
                .Select(r => { var d = new Dictionary<string, string>(); for (int i = 0; i < h.Length && i < r.Length; i++) d[h[i]] = r[i]; return d; })
                .ToList();
        }

        static double F(Dictionary<string, string> r, string k)
        {
            string s;
            double v;
            return r.TryGetValue(k, out s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }

        static string G(Dictionary<string, string> r, string k)
        {
            string s;
            return r.TryGetValue(k, out s) ? s : "";
        }

        public HistoryForm(string path)
        {
            List<KeyValuePair<string, string>> meta;
            string[] hdr;
            List<Dictionary<string, string>> data;
            LoadRun(path, out meta, out hdr, out data);
            Text = "지난 기록 — " + Path.GetFileName(path);
            Font = Ui.Font;
            Size = new Size(1150, 700);
            var samples = data.Where(r => G(r, "이벤트") == "").ToList();
            x = samples.Select(r => F(r, "경과초")).ToList();
            foreach (var c in hdr.Where(c => !Skip.Contains(c)))
                cols[c] = samples.Select(r => F(r, c)).ToList();

            var axis = chart.ChartAreas[0].AxisX;
            // 출탕 구간 음영
            foreach (var g in samples.Where(r => G(r, "출탕구간번호") != "").GroupBy(r => G(r, "출탕구간번호")))
            {
                double a = g.Min(r => F(r, "경과초")), b = g.Max(r => F(r, "경과초"));
                axis.StripLines.Add(new StripLine { IntervalOffset = a, StripWidth = Math.Max(0.01, b - a), BackColor = Ui.EjectShade, Interval = 0 });
            }
            // 이벤트 세로선
            foreach (var r in data.Where(r => G(r, "이벤트") != ""))
                axis.StripLines.Add(new StripLine
                {
                    IntervalOffset = F(r, "경과초"), StripWidth = 0, Interval = 0, BorderColor = Color.Gray, BorderWidth = 1,
                    Text = G(r, "이벤트"), ForeColor = Color.DimGray, TextAlignment = StringAlignment.Far, TextOrientation = TextOrientation.Rotated270,
                });

            foreach (var kv in cols)
            {
                if (kv.Value.All(double.IsNaN)) continue;   // 값이 하나도 없는 열은 뺀다
                list.Items.Add(kv.Key, Default.Any(d => kv.Key.StartsWith(d)));
            }
            list.ItemCheck += (s, e) => BeginInvoke((Action)Redraw);   // ItemCheck 는 바뀌기 전에 불린다

            var info = new Label
            {
                Text = meta.Count > 0 ? string.Join("   ", meta.Select(m => m.Key + ": " + m.Value)) : "(메타데이터 없음)",
                Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(1100, 0), Padding = new Padding(6),
            };
            Controls.Add(chart);
            Controls.Add(list);
            Controls.Add(info);
            Redraw();
        }

        void Redraw()
        {
            for (int i = 0; i < list.Items.Count; i++)
            {
                string c = (string)list.Items[i];
                Series s;
                if (list.GetItemChecked(i) && !curves.ContainsKey(c))
                {
                    s = new Series(c) { ChartType = SeriesChartType.FastLine, BorderWidth = 2, Color = Ui.Colors[curves.Count % Ui.Colors.Length] };
                    Ui.SetPoints(s, x, cols[c]);
                    chart.Series.Add(s);
                    curves[c] = s;
                }
                else if (!list.GetItemChecked(i) && curves.TryGetValue(c, out s))
                {
                    chart.Series.Remove(s);
                    curves.Remove(c);
                }
            }
        }
    }

    // 태그 설정·환산 확인 (SPEC 9장). tags.csv 를 표로 고치고, HMI 표시값을 넣으면 배율을 제안한다.
    // 저장하면 다시 시작해야 반영된다 (읽기 묶음이 시작 때 정해지므로).
    class TagEditorForm : Form
    {
        static readonly string[] Live = { "PLC 원시값", "환산값(표 배율)", "HMI 값 입력" };
        readonly string path;
        readonly Action onSaved;
        readonly string[] header;
        readonly Dictionary<string, int> col;
        readonly int cRaw, cVal, cHmi;
        readonly DataGridView t = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells, SelectionMode = DataGridViewSelectionMode.CellSelect,
        };
        Dictionary<string, long> raws = new Dictionary<string, long>();

        public TagEditorForm(string path, Action onSaved)
        {
            this.path = path; this.onSaved = onSaved;
            Text = "태그 설정 · 환산 확인";
            Font = Ui.Font;
            Size = new Size(1300, 720);
            var rows = Csv.Read(path);
            header = rows[0];
            col = header.Select((h, i) => new { h, i }).ToDictionary(p => p.h, p => p.i);
            int n = header.Length;
            cRaw = n; cVal = n + 1; cHmi = n + 2;
            foreach (var h in header.Concat(Live)) t.Columns.Add(h, h);
            foreach (DataGridViewColumn c in t.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
            t.Columns[col["key"]].ReadOnly = true;   // key 는 기록 파일 열 이름이라 못 바꾼다
            foreach (int j in new[] { cRaw, cVal })
            {
                t.Columns[j].ReadOnly = true;
                t.Columns[j].DefaultCellStyle.BackColor = Color.FromArgb(240, 240, 240);
            }
            foreach (var r in rows.Skip(1))
            {
                var cells = new object[n + Live.Length];
                for (int j = 0; j < n; j++) cells[j] = j < r.Length ? r[j] : "";
                cells[cRaw] = cells[cVal] = "—";
                cells[cHmi] = "";
                t.Rows.Add(cells);
            }

            var guide = new Label
            {
                Text = "① 현장 HMI 화면의 값을 'HMI 값 입력' 칸에 적는다  ② [배율 제안]을 누르면 배율 칸이 (HMI−오프셋)÷원시값으로 바뀐다"
                    + "  ③ 맞으면 '확인필요'를 N 으로  ④ [저장 후 다시 시작]. 원시값은 1초마다 새로 읽힌다.",
                Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(1250, 0), Padding = new Padding(6),
            };
            var bSuggest = new Button { Text = "배율 제안 (HMI 값 넣은 줄만)", AutoSize = true };
            bSuggest.Click += (s, e) => Suggest();
            var bSave = new Button { Text = "저장 후 다시 시작", AutoSize = true };
            bSave.Click += (s, e) => Save();
            var bClose = new Button { Text = "닫기 (저장 안 함)", AutoSize = true };
            bClose.Click += (s, e) => Hide();
            var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
            btns.Controls.AddRange(new Control[] { bSuggest, bSave, bClose });
            Controls.Add(t);
            Controls.Add(guide);
            Controls.Add(btns);
        }

        // 사용자가 닫으면 숨기기만 한다 (고친 값 유지)
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }

        string Cell(int i, int j) { return Convert.ToString(t.Rows[i].Cells[j].Value ?? "").Trim(); }

        double Num(int i, string h) { return double.Parse(Cell(i, col[h]), NumberStyles.Float, CultureInfo.InvariantCulture); }

        // 메인 창이 새 값을 읽을 때마다 부른다
        public void SetRaws(Dictionary<string, long> raws)
        {
            this.raws = raws;
            for (int i = 0; i < t.Rows.Count; i++)
            {
                long raw;
                bool has = raws.TryGetValue(Cell(i, col["key"]), out raw);
                t.Rows[i].Cells[cRaw].Value = has ? raw.ToString(CultureInfo.InvariantCulture) : "—";
                string val;
                try { val = has ? (raw * Num(i, "배율") + Num(i, "오프셋")).ToString("G6", CultureInfo.InvariantCulture) : "—"; }
                catch (FormatException) { val = "배율/오프셋 숫자 아님"; }
                t.Rows[i].Cells[cVal].Value = val;
            }
        }

        void Suggest()
        {
            t.EndEdit();
            int done = 0;
            var skipped = new List<string>();
            for (int i = 0; i < t.Rows.Count; i++)
            {
                string key = Cell(i, col["key"]), hmi = Cell(i, cHmi);
                if (hmi == "") continue;
                double? s = null;
                long raw;
                double h;
                try
                {
                    if (raws.TryGetValue(key, out raw) && double.TryParse(hmi, NumberStyles.Float, CultureInfo.InvariantCulture, out h))
                        s = Tags.SuggestScale(raw, h, Num(i, "오프셋"));
                }
                catch (FormatException) { }
                if (s == null) { skipped.Add(key); continue; }   // 원시값 없음·0, 숫자 아님
                t.Rows[i].Cells[col["배율"]].Value = s.Value.ToString("R", CultureInfo.InvariantCulture);
                done++;
            }
            SetRaws(raws);
            string msg = done + "줄 배율을 바꿨습니다.";
            if (skipped.Count > 0) msg += "\n계산 못 한 줄(원시값 0·없음 또는 숫자 아님): " + string.Join(", ", skipped);
            MessageBox.Show(this, msg, "배율 제안");
        }

        void Save()
        {
            t.EndEdit();
            var rows = Enumerable.Range(0, t.Rows.Count).Select(i => header.Select((h, j) => Cell(i, j)).ToArray()).ToList();
            try { Tags.Save(path, header, rows); }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, "저장 안 됨", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Hide();
            onSaved();
        }
    }

    // PLC 접속 설정 — settings.toml 의 [plc] host·port·timeout_s 를 고친다. 저장 전 그 값으로 읽기 시험 가능.
    class PlcSettingsForm : Form
    {
        readonly string path;
        readonly TextBox tHost = new TextBox { Width = 160 }, tPort = new TextBox { Width = 80 }, tTimeout = new TextBox { Width = 80 };
        readonly TextBox tPcIp = new TextBox { Width = 160 }, tPcMask = new TextBox { Width = 160, Text = "255.255.255.0" };
        readonly ComboBox cAdapter = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Label pcLbl = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(400, 0) };
        List<System.Net.NetworkInformation.NetworkInterface> adapters;

        public PlcSettingsForm(string path)
        {
            this.path = path;
            Text = "PLC 접속 설정";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var cfg = Settings.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8).TrimStart('\uFEFF'));
            tHost.Text = cfg.Str("plc.host");
            tPort.Text = cfg.Num("plc.port").ToString(App.Inv);
            tTimeout.Text = cfg.Num("plc.timeout_s").ToString(App.Inv);
            try { tPcIp.Text = cfg.Str("pc.ip"); } catch (KeyNotFoundException) { tPcIp.Text = "192.168.1.50"; }

            var g = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(12) };
            Action<string, Control> row = (label, c) =>
            {
                g.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) });
                g.Controls.Add(c);
            };
            row("PLC IP 주소", tHost);
            row("포트 (보통 2004)", tPort);
            row("응답 대기 (초)", tTimeout);
            var bTest = new Button { Text = "연결 시험", AutoSize = true };
            var bSave = new Button { Text = "저장", AutoSize = true };
            bTest.Click += (s, e) => Test();
            bSave.Click += (s, e) => Save();
            var btns = new FlowLayoutPanel { AutoSize = true };
            btns.Controls.AddRange(new Control[] { bTest, bSave });
            g.Controls.Add(btns); g.SetColumnSpan(btns, 2);

            var head = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 14, 3, 3),
                Text = "노트북 IP (바꿀 때 관리자 권한 확인 창이 뜹니다)" };
            g.Controls.Add(head); g.SetColumnSpan(head, 2);
            row("연결 (랜 어댑터)", cAdapter);
            row("노트북 IP", tPcIp);
            row("서브넷 마스크", tPcMask);
            var bApply = new Button { Text = "고정 IP 적용", AutoSize = true };
            var bDhcp = new Button { Text = "자동(DHCP)으로 되돌리기", AutoSize = true };
            bApply.Click += (s, e) => ApplyPcIp();
            bDhcp.Click += (s, e) => ApplyDhcp();
            var pcBtns = new FlowLayoutPanel { AutoSize = true };
            pcBtns.Controls.AddRange(new Control[] { bApply, bDhcp });
            g.Controls.Add(pcBtns); g.SetColumnSpan(pcBtns, 2);
            g.Controls.Add(pcLbl); g.SetColumnSpan(pcLbl, 2);

            var bCancel = new Button { Text = "닫기", AutoSize = true, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right };
            g.Controls.Add(bCancel); g.SetColumnSpan(bCancel, 2);
            Controls.Add(g);
            CancelButton = bCancel;
            LoadAdapters();
        }

        void LoadAdapters()
        {
            adapters = NetCheck.Adapters();
            cAdapter.Items.Clear();
            foreach (var n in adapters)
                cAdapter.Items.Add(n.Name + " — " + (n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up ? "연결됨" : "연결 안 됨")
                    + " (" + n.Description + ")");
            if (cAdapter.Items.Count > 0) cAdapter.SelectedIndex = 0;   // 목록이 유선 먼저
            pcLbl.Text = "이 PC 주소: " + string.Join(", ", NetCheck.LocalAddrs())
                + "\n랜선을 꽂은 유선 연결을 고르세요. 현장에서는 와이파이를 끄는 것이 안전합니다.";
        }

        // 노트북 IP 입력 검사. 맞으면 null
        public static string CheckPcIp(string ip, string mask, string plcHost)
        {
            System.Net.IPAddress a;
            if (ip.Split('.').Length != 4 || !System.Net.IPAddress.TryParse(ip, out a)) return "노트북 IP 형식이 틀렸습니다 (예: 192.168.1.50).";
            if (mask.Split('.').Length != 4 || !System.Net.IPAddress.TryParse(mask, out a)) return "서브넷 마스크 형식이 틀렸습니다 (보통 255.255.255.0).";
            if (ip == plcHost) return "PLC와 같은 주소는 쓸 수 없습니다.";
            if (!NetCheck.SameNet(ip, plcHost, mask)) return "PLC(" + plcHost + ")와 대역이 다릅니다. 같은 대역(예: 앞 세 자리가 같은 주소)으로 넣으세요.";
            return null;
        }

        System.Net.NetworkInformation.NetworkInterface Selected()
        {
            if (cAdapter.SelectedIndex < 0) { MessageBox.Show(this, "연결(랜 어댑터)을 고르세요.", Text); return null; }
            return adapters[cAdapter.SelectedIndex];
        }

        void ApplyPcIp()
        {
            var ni = Selected();
            if (ni == null) return;
            string ip = tPcIp.Text.Trim(), mask = tPcMask.Text.Trim(), plc = tHost.Text.Trim();
            string err = CheckPcIp(ip, mask, plc);
            if (err != null) { MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (MessageBox.Show(this, "'" + ni.Name + "' 연결을 고정 IP " + ip + " / " + mask + " (게이트웨이 없음)으로 바꿉니다.\n"
                + "이 연결로 인터넷을 쓰고 있었다면 인터넷이 끊길 수 있습니다. 나중에 '자동(DHCP)으로 되돌리기'로 돌아갈 수 있습니다.\n계속할까요?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Cursor = Cursors.WaitCursor;
            string r = NetCheck.RunNetshAdmin(NetCheck.NetshStatic(ni.Name, ip, mask));
            Cursor = Cursors.Default;
            if (r != null) { MessageBox.Show(this, r, "노트북 IP 바꾸기 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            try
            {
                string s = File.ReadAllText(path, System.Text.Encoding.UTF8);
                File.WriteAllText(path, Settings.SetValue(s, "pc", "ip", "\"" + ip + "\""), new System.Text.UTF8Encoding(false));
            }
            catch (Exception) { }   // 기록용 값이라 저장 실패는 무시 (IP는 이미 바뀜)
            System.Threading.Thread.Sleep(1500);   // 윈도우가 새 주소를 올릴 시간
            LoadAdapters();
            string msg = "노트북 IP를 " + ip + " 로 바꿨습니다.";
            var others = NetCheck.OthersOnNet(ni.Id, plc);
            if (others.Count > 0) msg += "\n\n주의: 다른 연결(" + string.Join(", ", others) + ")도 같은 대역입니다. PLC 통신이 그쪽으로 샐 수 있으니 와이파이 등을 끄세요.";
            MessageBox.Show(this, msg, Text);
        }

        void ApplyDhcp()
        {
            var ni = Selected();
            if (ni == null) return;
            if (MessageBox.Show(this, "'" + ni.Name + "' 연결을 자동(DHCP)으로 되돌릴까요?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            Cursor = Cursors.WaitCursor;
            string r = NetCheck.RunNetshAdmin(NetCheck.NetshDhcp(ni.Name));
            Cursor = Cursors.Default;
            if (r != null) { MessageBox.Show(this, r, "되돌리기 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            System.Threading.Thread.Sleep(1500);
            LoadAdapters();
            MessageBox.Show(this, "자동(DHCP)으로 되돌렸습니다.", Text);
        }

        // 입력 검사. 맞으면 null, 틀리면 안내 문구
        public static string Check(string host, string port, string timeout, out int p, out double t)
        {
            p = 0; t = 0;
            System.Net.IPAddress ip;
            if (host.Split('.').Length != 4 || !System.Net.IPAddress.TryParse(host, out ip)) return "IP 주소 형식이 틀렸습니다 (예: 192.168.1.2).";
            if (!int.TryParse(port, NumberStyles.None, App.Inv, out p) || p < 1 || p > 65535) return "포트는 1~65535 숫자입니다.";
            if (!double.TryParse(timeout, NumberStyles.Float, App.Inv, out t) || t < 0.2 || t > 30) return "응답 대기는 0.2~30초입니다.";
            return null;
        }

        void Test()
        {
            int p; double t;
            string host = tHost.Text.Trim();
            string err = Check(host, tPort.Text.Trim(), tTimeout.Text.Trim(), out p, out t);
            if (err != null) { MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            var c = new XgtClient(host, p, t);
            string msg;
            Cursor = Cursors.WaitCursor;
            try { c.Connect(); msg = "접속 성공. D401 = " + c.ReadWord("D401"); }
            catch (Exception e)
            {
                msg = "접속 실패: " + e.Message;
                string hint = NetCheck.SubnetHint(host);
                if (hint != "") msg += "\n→ " + hint;
            }
            finally { c.Close(); Cursor = Cursors.Default; }
            MessageBox.Show(this, msg, "연결 시험");
        }

        void Save()
        {
            int p; double t;
            string host = tHost.Text.Trim();
            string err = Check(host, tPort.Text.Trim(), tTimeout.Text.Trim(), out p, out t);
            if (err != null) { MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            try
            {
                string s = File.ReadAllText(path, System.Text.Encoding.UTF8);
                s = Settings.SetValue(s, "plc", "host", "\"" + host + "\"");
                s = Settings.SetValue(s, "plc", "port", p.ToString(App.Inv));
                s = Settings.SetValue(s, "plc", "timeout_s", t.ToString(App.Inv));
                Settings.Parse(s.TrimStart('\uFEFF'));   // 다시 읽히는지 확인한 뒤 저장
                File.WriteAllText(path, s, new System.Text.UTF8Encoding(false));
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, "저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            DialogResult = DialogResult.OK;
        }
    }
}

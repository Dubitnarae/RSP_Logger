// 메인 화면 (SPEC 9장): 상단 상태줄·좌측 값 패널·중앙 추세 그래프·우측 Run 입력과 기록 버튼

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace RspLogger
{
    static class Ui
    {
        public static readonly Font Font = new Font("맑은 고딕", 9F);
        public static readonly Color[] Colors = new[] { "#1f77b4", "#d62728", "#2ca02c", "#ff7f0e", "#9467bd", "#8c564b", "#e377c2", "#17becf" }
            .Select(h => ColorTranslator.FromHtml(h)).ToArray();
        public static readonly Color EjectShade = Color.FromArgb(50, 255, 165, 0);

        public static Chart NewChart()
        {
            var chart = new Chart { Dock = DockStyle.Fill, BackColor = Color.White };
            var a = new ChartArea("a");
            a.AxisX.Title = "경과 시간 (s)";
            a.AxisX.LabelStyle.Format = "0";
            a.AxisY.IsStartedFromZero = false;
            a.AxisX.MajorGrid.LineColor = a.AxisY.MajorGrid.LineColor = Color.FromArgb(225, 225, 225);
            chart.ChartAreas.Add(a);
            chart.Legends.Add(new Legend { Docking = Docking.Top, Font = Font });
            return chart;
        }

        // 값 없는 점(NaN)은 빼고 그린다
        public static void SetPoints(Series s, IList<double> x, IList<double> y)
        {
            var xs = new List<double>();
            var ys = new List<double>();
            for (int i = 0; i < x.Count && i < y.Count; i++)
                if (!double.IsNaN(x[i]) && !double.IsNaN(y[i])) { xs.Add(x[i]); ys.Add(y[i]); }
            s.Points.DataBindXY(xs, ys);
        }
    }

    class MainForm : Form
    {
        const int History = 3600;   // 그래프에 남기는 점 수
        static readonly HashSet<string> DefaultPlot = new HashSet<string> { "p_upper", "p_lower", "dp" };

        readonly string host;
        readonly double wheelDmm;
        readonly Recorder recorder;
        readonly bool dev;
        public string RestartArgs;   // 창이 닫힌 뒤 이 인수로 다시 시작 (null 이면 안 함)
        public Poller Poller;
        public double FastInterval = 0.2;

        bool connected;
        string connMsg = "접속 중…";
        Dictionary<string, double> last = new Dictionary<string, double>();
        double lastTs;
        double? tStart;

        readonly Label statusLbl;
        readonly ToolStripStatusLabel msgLbl = new ToolStripStatusLabel();
        readonly ToolTip tip = new ToolTip();
        readonly Dictionary<string, CheckBox> chk = new Dictionary<string, CheckBox>();
        readonly Dictionary<string, Label> valLbl = new Dictionary<string, Label>();
        readonly Chart chart;
        readonly List<double> tHist = new List<double>();
        readonly Dictionary<string, List<double>> hist = new Dictionary<string, List<double>>();
        readonly Dictionary<string, Series> curves = new Dictionary<string, Series>();
        readonly Dictionary<int, StripLine> regions = new Dictionary<int, StripLine>();   // 출탕번호 -> 그래프 음영
        readonly Dictionary<string, TextBox> meta = new Dictionary<string, TextBox>();
        readonly Button btnStart, btnStop, btnEjStart, btnEjEnd;
        TagEditorForm tagEditor;

        public MainForm(List<Tag> tags, string host, double wheelDmm, Recorder recorder, bool dev)
        {
            this.host = host; this.wheelDmm = wheelDmm; this.recorder = recorder; this.dev = dev;
            Text = App.Name + " v" + App.Version + (dev ? "  [개발 모드: 가상 PLC]" : "");
            Font = Ui.Font;
            Size = new Size(1300, 780);
            StartPosition = FormStartPosition.CenterScreen;

            // 값 패널 — 계산 항목 + 비트가 아닌 태그 (비트 태그는 상태줄에만)
            var rows = Calc.Derived.Concat(tags.Where(t => t.Type != "BIT").Cast<Col>()).ToList();
            var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(4) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foreach (var c in rows)
            {
                var cb = new CheckBox { Text = c.Name + (c.Unconfirmed ? " *" : ""), Checked = DefaultPlot.Contains(c.Key), AutoSize = true };
                if (c.Unconfirmed) tip.SetToolTip(cb, "환산 배율·식이 현장 확인 전 임시값");
                cb.CheckedChanged += (s, e) => Redraw();
                // 크기 고정 — 글자가 바뀔 때마다 표 전체를 다시 배치하지 않게 (0.2초마다 바뀜)
                var v = new Label { Text = "—", AutoSize = false, Size = new Size(120, 22), TextAlign = ContentAlignment.MiddleRight, Font = new Font("Consolas", 11F, FontStyle.Bold) };
                grid.Controls.Add(cb);
                grid.Controls.Add(v);
                grid.Controls.Add(new Label { Text = c.Unit, AutoSize = true, Anchor = AnchorStyles.Left });
                chk[c.Key] = cb;
                valLbl[c.Key] = v;
                hist[c.Key] = new List<double>();
            }
            var note = new Label { Text = "* 현장 확인 전 임시 환산\n체크한 항목만 그래프에 표시", AutoSize = true, ForeColor = Color.DimGray };
            grid.Controls.Add(note);
            grid.SetColumnSpan(note, 3);
            var left = new Panel { Dock = DockStyle.Left, Width = 380, AutoScroll = true };
            left.Controls.Add(grid);

            // Run 입력 — 오른쪽
            var form = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(6) };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var k in Recorder.MetaFields)
            {
                form.Controls.Add(new Label { Text = k, AutoSize = true, Anchor = AnchorStyles.Left });
                meta[k] = new TextBox { Dock = DockStyle.Fill };
                form.Controls.Add(meta[k]);
            }
            if (wheelDmm != 0) meta["휠 지름(mm)"].Text = wheelDmm.ToString("G", App.Inv);
            Func<string, EventHandler, Button> addBtn = (text, h) =>
            {
                var b = new Button { Text = text, Dock = DockStyle.Fill, Height = 36 };
                b.Click += h;
                form.Controls.Add(b);
                form.SetColumnSpan(b, 2);
                return b;
            };
            btnStart = addBtn("● 기록 시작", (s, e) => StartRun());
            btnStop = addBtn("■ 기록 정지", (s, e) => StopRun(true));
            btnEjStart = addBtn("▲ 출탕 시작 (수동)", (s, e) => { recorder.EjectStart(lastTs != 0 ? lastTs : App.Now()); AfterChange(); });
            btnEjEnd = addBtn("▼ 출탕 종료", (s, e) => { recorder.EjectEnd(lastTs != 0 ? lastTs : App.Now()); AfterChange(); });
            btnEjEnd.Font = new Font(Ui.Font, FontStyle.Bold);
            foreach (var t in new[] { "출탕 시작: M151 자동 감지 / 종료: 버튼", "저장 폴더:\n" + recorder.OutDir })
            {
                var l = new Label { Text = t, AutoSize = true, MaximumSize = new Size(280, 0), Padding = new Padding(0, 6, 0, 0) };
                form.Controls.Add(l);
                form.SetColumnSpan(l, 2);
            }
            var right = new Panel { Dock = DockStyle.Right, Width = 310 };
            right.Controls.Add(form);

            chart = Ui.NewChart();
            statusLbl = new Label { Dock = DockStyle.Top, Height = 32, Font = new Font(Ui.Font.FontFamily, 10.5F), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
            var footer = new Label { Text = "개발자 " + App.Author, Dock = DockStyle.Bottom, Height = 18, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.Gray };
            var strip = new StatusStrip();
            strip.Items.Add(msgLbl);

            var menu = new MenuStrip();
            var file = new ToolStripMenuItem("파일");
            file.DropDownItems.Add("지난 기록 열기…", null, (s, e) => OpenHistory());
            file.DropDownItems.Add("태그 설정 · 환산 확인…", null, (s, e) => OpenTagEditor());
            file.DropDownItems.Add("PLC 접속 설정 (IP)…", null, (s, e) => OpenPlcSettings());
            file.DropDownItems.Add("HMI 원본 파일 지정…", null, (s, e) => { if (HmiReplayForm.ChooseTds(this) != null) Restart(dev, "HMI 원본 파일 지정됨", "다시 시작하면 HMI 화면 값도 같이 기록합니다. 지금 다시 시작할까요?"); });
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(dev ? "현장 모드로 다시 시작" : "개발 모드(가상 PLC)로 다시 시작", null, (s, e) => SwitchMode());
            var help = new ToolStripMenuItem("도움말");
            help.DropDownItems.Add("프로그램 정보", null, (s, e) => MessageBox.Show(this,
                App.Name + " v" + App.Version + "\n개발자 " + App.Author + "\n\nPLC 공정값 읽기 전용 기록기", App.Name));
            menu.Items.Add(file);
            menu.Items.Add(help);
            MainMenuStrip = menu;

            // 도킹은 나중에 넣은 것이 바깥쪽 — 가운데(Fill)를 먼저 넣는다
            Controls.Add(chart);
            Controls.Add(left);
            Controls.Add(right);
            Controls.Add(statusLbl);
            Controls.Add(menu);
            Controls.Add(footer);
            Controls.Add(strip);

            Shown += (s, e) => CheckUpdate();
            SyncButtons();
            UpdateStatus();
        }

        // ---------- 읽기 스레드 → 화면 스레드 ----------
        void Post(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
            catch (InvalidOperationException) { }   // 창이 닫히는 중
        }

        public void OnDataFromThread(double ts, Dictionary<string, double> values, Dictionary<string, long> raws)
        {
            Post(() => OnData(ts, values, raws));
        }

        public void OnStatusFromThread(bool ok, string msg)
        {
            Post(() => OnStatus(ok, msg));
        }

        // ---------- 지난 기록 / 업데이트 ----------
        void OpenHistory()
        {
            string path;
            using (var d = new OpenFileDialog { Title = "지난 기록 열기", Filter = "CSV (*.csv)|*.csv", InitialDirectory = Directory.Exists(recorder.OutDir) ? recorder.OutDir : App.ExeDir })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                path = d.FileName;
            }
            try { new HistoryForm(path).Show(); }
            catch (Exception e) { MessageBox.Show(this, e.Message, "열 수 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        // 시작 시 한 번 백그라운드로 확인
        void CheckUpdate()
        {
            Task.Run(() =>
            {
                try
                {
                    var found = Updater.Check();
                    if (found != null) Post(() => OnUpdate(found));
                }
                catch { }   // 인터넷이 없는 현장 PC 등 — 조용히 넘어간다
            });
        }

        void OnUpdate(Updater.Found found)
        {
            if (recorder.Active) return;   // 기록 중에는 묻지 않는다
            string notes = found.Notes.Length > 800 ? found.Notes.Substring(0, 800) : found.Notes;
            if (MessageBox.Show(this, "새 버전 v" + found.Ver + " 이 있습니다. 지금 설치할까요?\n\n" + notes, "업데이트",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Cursor = Cursors.WaitCursor;
            try { Updater.Install(found); }
            catch (Exception e)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, e.Message, "업데이트 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            RestartArgs = dev ? "--dev" : "";
            Close();
        }

        // ---------- 기록 ----------
        void SyncButtons()
        {
            var r = recorder;
            foreach (var e in meta.Values) e.Enabled = !r.Active;
            btnStart.Enabled = !r.Active;
            btnStop.Enabled = r.Active;
            btnEjStart.Enabled = r.Active && !r.Ejecting;
            btnEjEnd.Enabled = r.Ejecting;
        }

        void AfterChange()
        {
            SyncButtons();
            UpdateStatus();
        }

        void OnStatus(bool ok, string msg)
        {
            if (connected && !ok && recorder.Active) recorder.Event(App.Now(), "연결 끊김: " + msg);
            connected = ok;
            connMsg = msg;
            UpdateStatus();
        }

        // Run 입력의 휠 지름이 숫자면 그것, 아니면 설정값
        double WheelD()
        {
            double d;
            return double.TryParse(meta["휠 지름(mm)"].Text, System.Globalization.NumberStyles.Float, App.Inv, out d) ? d : wheelDmm;
        }

        void StartRun()
        {
            if (meta["시료명"].Text.Trim() == "")
            {
                MessageBox.Show(this, "시료명을 입력해야 기록을 시작할 수 있습니다.", "시료명 필요");
                meta["시료명"].Focus();
                return;
            }
            string path;
            try { path = recorder.Start(App.Now(), meta.ToDictionary(kv => kv.Key, kv => kv.Value.Text)); }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, "기록 시작 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            chart.ChartAreas[0].AxisX.StripLines.Clear();
            regions.Clear();
            if (Poller != null) Poller.SetInterval(FastInterval);
            msgLbl.Text = "기록 중: " + Path.GetFileName(path);
            AfterChange();
        }

        void StopRun(bool ask)
        {
            var results = ask ? ResultDialog.Ask(this) : new Dictionary<string, string>();
            string path;
            try { path = recorder.Stop(App.Now(), results); }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, "저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (Poller != null) Poller.SetInterval(Poller.BaseInterval);
            if (path != null) msgLbl.Text = "저장됨: " + path;
            AfterChange();
        }

        void SwitchMode()
        {
            Restart(!dev, "모드 전환", (dev ? "현장 모드" : "개발 모드(가상 PLC)") + "로 다시 시작합니다.");
        }

        // 실제 재시작은 창이 닫힌 뒤 Program.Main 이 한다
        void Restart(bool toDev, string title, string msg)
        {
            if (recorder.Active) msg += "\n지금 기록 중인 Run은 정지·저장됩니다.";
            if (MessageBox.Show(this, msg, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            RestartArgs = toDev ? "--dev" : "";
            Close();
        }

        void OpenTagEditor()
        {
            if (tagEditor == null)
                tagEditor = new TagEditorForm(Path.Combine(App.ConfigDir, "tags.csv"), () => Restart(dev, "태그 저장됨",
                    "tags.csv 를 저장했습니다. 다시 시작해야 반영됩니다. 지금 다시 시작할까요?")) { Owner = this };
            tagEditor.Show();
            tagEditor.Activate();
        }

        void OpenPlcSettings()
        {
            using (var f = new PlcSettingsForm(Path.Combine(App.ConfigDir, "settings.toml")))
                if (f.ShowDialog(this) == DialogResult.OK)
                    Restart(dev, "PLC 접속 설정 저장됨", "settings.toml 을 저장했습니다. 다시 시작해야 반영됩니다. 지금 다시 시작할까요?"
                        + (dev ? "\n(지금은 개발 모드라 가상 PLC에 붙습니다. 새 주소는 현장 모드에서 씁니다.)" : ""));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 다시 시작(메뉴)일 때는 묻지 않는다. 윈도우 종료 중에는 창을 붙잡지 않는다.
            if (RestartArgs == null && e.CloseReason != CloseReason.WindowsShutDown && IpRestore.Pending && !AskIpRestore())
            {
                e.Cancel = true;
                return;
            }
            if (recorder.Active) StopRun(false);   // 닫을 때도 파일을 닫아 기록을 보존
            base.OnFormClosing(e);
        }

        // 종료할 때 노트북 IP를 원래대로 되돌릴지 묻는다. 종료를 취소하면 false.
        bool AskIpRestore()
        {
            string desc;
            try { desc = IpRestore.Describe(IpRestore.Load()); } catch (Exception) { desc = "(기억 파일을 읽지 못함)"; }
            var a = MessageBox.Show(this, "노트북 IP가 고정 IP로 바뀌어 있습니다.\n\n원래 설정: " + desc
                + "\n\n예: 원래 설정으로 되돌리고 종료 (관리자 권한 확인 창이 뜹니다)\n아니요: 고정 IP 유지하고 종료\n취소: 종료하지 않음",
                "노트북 IP 되돌리기", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (a == DialogResult.Cancel) return false;
            if (a == DialogResult.Yes)
            {
                string r = IpRestore.Restore();
                if (r != null)
                    return MessageBox.Show(this, "되돌리지 못했습니다: " + r + "\n\n그래도 종료할까요? (다음에 종료할 때 다시 묻습니다)",
                        "노트북 IP 되돌리기", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
            }
            return true;
        }

        // ---------- 화면 갱신 ----------
        void UpdateStatus()
        {
            var r = recorder;
            string rec = r.Active ? "● 기록 중 " + Math.Max(0, lastTs - r.T0).ToString("F0", App.Inv) + "초" : "대기(모니터링만)";
            if (r.Ejecting)
            {
                var e = r.Ejections[r.Ejections.Count - 1];
                rec += "   |   ■ 출탕 " + e.No + " 진행 중 " + Math.Max(0, lastTs - e.Start).ToString("F1", App.Inv) + "초";
            }
            else if (r.Active) rec += "   |   출탕 " + r.Ejections.Count + "회";
            Func<string, string> bit = k => last.ContainsKey(k) ? (last[k] != 0 ? "ON" : "OFF") : "—";
            statusLbl.Text = "● " + connMsg + "   |   PLC " + host + "   |   RSP 리모콘(M382): " + bit("rsp_remote")
                + "   |   스토퍼 상승(M151): " + bit("stopper_up") + "   |   기록: " + rec
                + (Poller != null && Poller.HmiError != null ? "   |   HMI 화면 기록 중지: " + Poller.HmiError : "");
            statusLbl.ForeColor = connected ? Color.DarkGreen : Color.Firebrick;
            statusLbl.BackColor = r.Ejecting ? Color.FromArgb(255, 225, 190) : r.Active ? Color.FromArgb(255, 238, 238) : SystemColors.Control;
        }

        void OnData(double ts, Dictionary<string, double> values, Dictionary<string, long> raws)
        {
            Calc.AddDerived(values, WheelD());
            last = values;
            lastTs = ts;
            recorder.Add(ts, values, raws);
            if (tagEditor != null && tagEditor.Visible) tagEditor.SetRaws(raws);
            if (tStart == null) tStart = ts;
            tHist.Add(ts - tStart.Value);
            if (tHist.Count > History) tHist.RemoveAt(0);
            foreach (var kv in valLbl)
            {
                double v;
                bool has = values.TryGetValue(kv.Key, out v);
                var h = hist[kv.Key];
                h.Add(has ? v : double.NaN);
                if (h.Count > History) h.RemoveAt(0);
                kv.Value.Text = !has ? "—" : kv.Key == "vg1" || kv.Key == "vg2" ? v.ToString("0.00e+00", App.Inv) : v.ToString("#,##0.00", App.Inv);
                long raw, prev;
                if (raws.TryGetValue(kv.Key, out raw) && !(tipRaw.TryGetValue(kv.Key, out prev) && prev == raw))
                {
                    tipRaw[kv.Key] = raw;
                    tip.SetToolTip(kv.Value, "PLC 원시값 " + raw + "  (HMI 값과 비교해 배율 확인용)");
                }
            }
            SyncButtons();
            UpdateStatus();
            if (redrawClock.ElapsedMilliseconds >= 500)   // 그래프는 0.5초에 한 번만 다시 그린다
            {
                redrawClock.Restart();
                SyncRegions();
                Redraw();
            }
        }

        readonly Dictionary<string, long> tipRaw = new Dictionary<string, long>();
        readonly System.Diagnostics.Stopwatch redrawClock = System.Diagnostics.Stopwatch.StartNew();

        // 출탕 구간을 그래프에 주황 음영으로 표시
        void SyncRegions()
        {
            if (tStart == null) return;
            foreach (var e in recorder.Ejections)
            {
                StripLine s;
                if (!regions.TryGetValue(e.No, out s))
                {
                    s = new StripLine { BackColor = Ui.EjectShade, Interval = 0 };
                    chart.ChartAreas[0].AxisX.StripLines.Add(s);
                    regions[e.No] = s;
                }
                s.IntervalOffset = e.Start - tStart.Value;
                s.StripWidth = Math.Max(0.01, (e.End ?? lastTs) - e.Start);
            }
        }

        void Redraw()
        {
            foreach (var kv in chk)
            {
                Series s;
                if (kv.Value.Checked)
                {
                    if (!curves.TryGetValue(kv.Key, out s))
                    {
                        s = new Series(kv.Value.Text) { ChartType = SeriesChartType.FastLine, BorderWidth = 2, Color = Ui.Colors[curves.Count % Ui.Colors.Length] };
                        chart.Series.Add(s);
                        curves[kv.Key] = s;
                    }
                    Ui.SetPoints(s, tHist, hist[kv.Key]);
                }
                else if (curves.TryGetValue(kv.Key, out s))
                {
                    chart.Series.Remove(s);
                    curves.Remove(kv.Key);
                }
            }
        }
    }

    // Run 정지 후 결과 입력. 모두 선택 입력이라 비워 두고 저장해도 된다.
    class ResultDialog : Form
    {
        readonly Dictionary<string, TextBox> edits = new Dictionary<string, TextBox>();

        ResultDialog()
        {
            Text = "Run 결과 입력 (비워 둬도 됨)";
            Font = Ui.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(10) };
            foreach (var k in Recorder.ResultFields)
            {
                t.Controls.Add(new Label { Text = k, AutoSize = true, Anchor = AnchorStyles.Left });
                edits[k] = new TextBox { Width = 220 };
                t.Controls.Add(edits[k]);
            }
            var ok = new Button { Text = "저장", DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right };
            t.Controls.Add(new Label());
            t.Controls.Add(ok);
            AcceptButton = ok;
            Controls.Add(t);
        }

        public static Dictionary<string, string> Ask(IWin32Window owner)
        {
            using (var d = new ResultDialog())
            {
                d.ShowDialog(owner);   // 창을 그냥 닫아도 입력한 만큼 저장 (기록은 이미 정지됨)
                return d.edits.ToDictionary(kv => kv.Key, kv => kv.Value.Text);
            }
        }
    }
}

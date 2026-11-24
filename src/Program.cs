// RSP Logger - 리본 멜트스피너 공정값을 LS XGB PLC에서 읽기 전용으로 수집·기록하는 프로그램.
// 실행: RSP_Logger.exe            (현장 PLC, exe 옆 config\settings.toml)
//       RSP_Logger.exe --dev      (개발 모드: 값이 움직이는 가상 PLC를 함께 띄움)
//       RSP_Logger.exe --conntest [--host IP] [--port N] D401 M382 ...   (연결 테스트)
//       RSP_Logger.exe --selftest (자체 시험, 빌드 때 실행)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("RSP Logger")]
[assembly: System.Reflection.AssemblyProduct("RSP Logger")]
[assembly: System.Reflection.AssemblyCompany("KIM YEONGDO")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 KIM YEONGDO")]
[assembly: System.Reflection.AssemblyVersion("0.5.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.5.1.0")]

namespace RspLogger
{
    static class App
    {
        public const string Name = "RSP Logger";
        public const string Author = "KIM YEONGDO";
        public const string Repo = "Dubitnarae/RSP_Logger";   // 자동 업데이트가 릴리스를 확인하는 저장소(공개여야 함)
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Version
        {
            get { var v = typeof(App).Assembly.GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
        }

        public static string ExeDir { get { return Path.GetDirectoryName(Application.ExecutablePath); } }
        public static string ConfigDir { get { return Path.Combine(ExeDir, "config"); } }

        // exe 옆 config 폴더를 쓴다 (현장에서 tags.csv·settings.toml 수정 가능). 없는 파일은 기본값을 꺼내 둔다.
        public static void EnsureConfig()
        {
            Directory.CreateDirectory(ConfigDir);
            foreach (var n in new[] { "settings.toml", "tags.csv" })
            {
                string p = Path.Combine(ConfigDir, n);
                if (!File.Exists(p)) File.WriteAllBytes(p, DefaultBytes(n));
            }
        }

        public static byte[] DefaultBytes(string name)
        {
            using (var s = typeof(App).Assembly.GetManifestResourceStream(name))
            using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
        }

        // 시각은 파이썬판과 같이 1970년부터의 초(double)로 다룬다
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static double Now() { return (DateTime.UtcNow - Epoch).TotalSeconds; }
        public static DateTime Local(double ts) { return Epoch.AddSeconds(ts).ToLocalTime(); }
        public static string Stamp(double ts) { return Local(ts).ToString("yyyy-MM-dd HH:mm:ss.fff", Inv); }

        public static void Restart(string args)
        {
            Process.Start(new ProcessStartInfo(Application.ExecutablePath, args) { UseShellExecute = false, WorkingDirectory = ExeDir });
        }
    }

    // settings.toml 읽기 — 이 프로그램이 쓰는 범위([구역], 키 = "문자열" 또는 숫자, # 주석)만
    class Settings
    {
        readonly Dictionary<string, string> v = new Dictionary<string, string>();

        public static Settings Parse(string text)
        {
            var s = new Settings();
            string sec = "";
            foreach (var line0 in text.Split('\n'))
            {
                string line = StripComment(line0).Trim();
                if (line == "") continue;
                if (line.StartsWith("[")) { sec = line.Trim('[', ']').Trim(); continue; }
                int eq = line.IndexOf('=');
                if (eq < 0) throw new FormatException("settings.toml 형식 오류: " + line);
                s.v[sec + "." + line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return s;
        }

        static string StripComment(string l)
        {
            bool q = false;
            for (int i = 0; i < l.Length; i++)
            {
                if (l[i] == '"' || l[i] == '\'') q = !q;
                else if (l[i] == '#' && !q) return l.Substring(0, i);
            }
            return l;
        }

        string Raw(string k)
        {
            string r;
            if (!v.TryGetValue(k, out r)) throw new KeyNotFoundException("settings.toml 에 " + k + " 항목이 없습니다.");
            return r;
        }

        public string Str(string k)
        {
            string r = Raw(k);
            if (r.Length >= 2 && r[0] == '\'' && r[r.Length - 1] == '\'') return r.Substring(1, r.Length - 2);
            if (r.Length >= 2 && r[0] == '"' && r[r.Length - 1] == '"') return r.Substring(1, r.Length - 2).Replace("\\\\", "\\").Replace("\\\"", "\"");
            return r;
        }

        public double Num(string k) { return double.Parse(Raw(k), NumberStyles.Float, App.Inv); }

        // [sec] 안 key 줄의 값만 바꾼 글을 돌려준다 (다른 줄·뒤 주석은 그대로). 키가 없으면 예외.
        public static string SetValue(string text, string sec, string key, string value)
        {
            var lines = text.Split('\n');
            string cur = "";
            for (int i = 0; i < lines.Length; i++)
            {
                string body = StripComment(lines[i]), t = body.Trim();
                if (t.StartsWith("[")) { cur = t.Trim('[', ']').Trim(); continue; }
                int eq = body.IndexOf('=');
                if (cur != sec || eq < 0 || body.Substring(0, eq).Trim() != key) continue;
                bool cr = lines[i].EndsWith("\r");
                string rest = lines[i].Substring(body.Length).TrimEnd('\r');
                lines[i] = body.Substring(0, eq + 1) + " " + value + (rest == "" ? "" : "  " + rest) + (cr ? "\r" : "");
                return string.Join("\n", lines);
            }
            throw new KeyNotFoundException("settings.toml 에 " + sec + "." + key + " 항목이 없습니다.");
        }
    }

    // CSV — 파이썬 csv 모듈과 같은 규칙(필요할 때만 따옴표, 줄 끝 \r\n)
    static class Csv
    {
        static string Field(string s)
        {
            s = s ?? "";
            return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        public static void Row(TextWriter w, IEnumerable<string> fields)
        {
            w.Write(string.Join(",", fields.Select(Field)));
            w.Write("\r\n");
        }

        public static List<string[]> Read(string path) { return Parse(File.ReadAllText(path, Encoding.UTF8)); }

        public static List<string[]> Parse(string text)
        {
            var rows = new List<string[]>();
            var row = new List<string>();
            var cur = new StringBuilder();
            bool q = false, any = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (q)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cur.Append('"'); i++; }
                    else if (c == '"') q = false;
                    else cur.Append(c);
                    continue;
                }
                if (c == '"') { q = true; any = true; }
                else if (c == ',') { row.Add(cur.ToString()); cur.Clear(); any = true; }
                else if (c == '\r') { }
                else if (c == '\n')
                {
                    if (any || cur.Length > 0) { row.Add(cur.ToString()); rows.Add(row.ToArray()); }
                    row = new List<string>(); cur.Clear(); any = false;
                }
                else { cur.Append(c); any = true; }
            }
            if (any || cur.Length > 0) { row.Add(cur.ToString()); rows.Add(row.ToArray()); }
            return rows;
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Contains("--selftest")) return SelfTest.Run();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--conntest")) return ConnTest(args);

            bool dev = args.Contains("--dev");
            MainForm win;
            Poller poller;
            FakePlc plc = null;
            try
            {
                App.EnsureConfig();
                var cfg = Settings.Parse(File.ReadAllText(Path.Combine(App.ConfigDir, "settings.toml"), Encoding.UTF8));
                string host = cfg.Str("plc.host");
                int port = (int)cfg.Num("plc.port");
                if (dev)
                {
                    plc = new FakePlc(0);
                    plc.StartAnimation(30, 5);
                    host = "127.0.0.1";
                    port = plc.Port;
                }
                var tags = Tags.Load(Path.Combine(App.ConfigDir, "tags.csv"));
                string dir = cfg.Str("record.dir");
                if (dir == "") dir = Path.Combine(App.ExeDir, dev ? "data_dev" : "data");
                var rec = new Recorder(dir, tags, cfg.Num("poll.interval_s"), cfg.Num("ejection.pre_s"), cfg.Num("ejection.post_s"));
                win = new MainForm(tags, host + ":" + port, cfg.Num("process.wheel_diameter_mm"), rec, dev);
                win.FastInterval = cfg.Num("ejection.fast_interval_s");
                poller = new Poller(new XgtClient(host, port, cfg.Num("plc.timeout_s")), tags, cfg.Num("poll.interval_s"),
                    win.OnDataFromThread, win.OnStatusFromThread);
                win.Poller = poller;
            }
            catch (Exception e)
            {
                MessageBox.Show("시작할 수 없습니다:\n" + e.Message + "\n\n설정 폴더: " + App.ConfigDir, App.Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            Updater.CleanupOld();
            poller.Start();
            Application.Run(win);
            poller.Stop();   // 소켓을 닫고 나서 다시 시작해야 PLC 연결이 2개가 되지 않는다 (CLAUDE.md 규칙 4)
            if (plc != null) plc.Stop();
            if (win.RestartArgs != null) App.Restart(win.RestartArgs);
            return 0;
        }

        // 현장 첫 접속용 — 몇 개 주소만 읽어 원시값을 보여준다
        static int ConnTest(string[] args)
        {
            var addrs = new List<string>();
            string host = null;
            int port = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--conntest") continue;
                if (args[i] == "--host" && i + 1 < args.Length) host = args[++i];
                else if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i], App.Inv);
                else addrs.Add(args[i]);
            }
            if (addrs.Count == 0) addrs.AddRange(new[] { "D401", "D402", "D324", "D221", "M382" });
            var sb = new StringBuilder();
            XgtClient c = null;
            try
            {
                App.EnsureConfig();
                var cfg = Settings.Parse(File.ReadAllText(Path.Combine(App.ConfigDir, "settings.toml"), Encoding.UTF8));
                host = host ?? cfg.Str("plc.host");
                if (port == 0) port = (int)cfg.Num("plc.port");
                c = new XgtClient(host, port, cfg.Num("plc.timeout_s"));
                sb.AppendLine("이 PC 주소: " + string.Join(", ", NetCheck.LocalAddrs()));
                sb.AppendLine("접속 시도: " + host + ":" + port);
                c.Connect();
                foreach (var a in addrs)
                {
                    try
                    {
                        if ("MPKFL".IndexOf(char.ToUpperInvariant(a[0])) >= 0)
                            sb.AppendLine(a + " = " + c.ReadBit(a));
                        else
                        {
                            int v = c.ReadWord(a);
                            sb.AppendLine(string.Format("{0} = {1} (부호 있음 {2}, 0x{1:X4})", a, v, (short)v));
                        }
                    }
                    catch (Exception e) { sb.AppendLine(a + " 읽기 실패: " + e.Message); }
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("접속 실패: " + e.Message);
                string hint = NetCheck.SubnetHint(host);
                if (hint != "") sb.AppendLine("→ " + hint);
                sb.AppendLine("→ 랜선·PC IP 대역(192.168.1.x)·PLC 전원을 확인하고 결과를 알려 주세요.");
            }
            finally { if (c != null) c.Close(); }
            MessageBox.Show(sb.ToString(), App.Name + " 연결 테스트");
            return 0;
        }
    }
}

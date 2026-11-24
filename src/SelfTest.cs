// 자체 시험 (RSP_Logger.exe --selftest) — 파이썬판 tests/ 를 옮긴 것. build.ps1 이 빌드 뒤 실행하고, 실패하면 게시를 막는다.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace RspLogger
{
    static class SelfTest
    {
        static int fails;
        static readonly StringBuilder log = new StringBuilder();
        static string tmpRoot;

        static void Check(bool ok, string what)
        {
            if (!ok) fails++;
            log.AppendLine((ok ? "  ok   " : "  FAIL ") + what);
        }

        static void Throws<T>(Action a, string what) where T : Exception
        {
            try { a(); Check(false, what + " (예외 없음)"); }
            catch (T) { Check(true, what); }
            catch (Exception e) { Check(false, what + " (다른 예외 " + e.GetType().Name + ")"); }
        }

        static bool Near(double a, double b, double eps = 1e-7) { return Math.Abs(a - b) < eps; }

        static string NewDir()
        {
            string d = Path.Combine(tmpRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        static string TagsPath()
        {
            string p = Path.Combine(NewDir(), "tags.csv");
            File.WriteAllBytes(p, App.DefaultBytes("tags.csv"));
            return p;
        }

        public static int Run()
        {
            tmpRoot = Path.Combine(Path.GetTempPath(), "rsp_logger_selftest");
            try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true); } catch (IOException) { }
            Directory.CreateDirectory(tmpRoot);
            var tests = new Dictionary<string, Action>
            {
                { "주소 해석", Address }, { "요청 프레임", Frame }, { "오류 응답·크기 제한", ErrorAndLimit },
                { "쓰기 명령 없음", NoWrite }, { "가상 PLC 왕복", SimRoundTrip }, { "배율 제안", SuggestScale },
                { "tags.csv 저장", SaveTags }, { "읽기 묶음", Blocks }, { "환산", Decode }, { "계산 항목", Derived },
                { "읽기 반복", Poll }, { "끊김 재접속", Backoff }, { "출탕 기록", EjectionRecording },
                { "출탕 없음·같은 이름", NoEjectionDupName }, { "run_index 열 변경", IndexHeaderChange },
                { "업데이트 버전 비교", VersionCompare }, { "업데이트 SHA256", Sha }, { "settings.toml", SettingsParse },
            };
            foreach (var t in tests)
            {
                log.AppendLine("[" + t.Key + "]");
                try { t.Value(); }
                catch (Exception e) { Check(false, "예외: " + e); }
            }
            log.AppendLine(fails == 0 ? "SELFTEST OK" : "SELFTEST FAILED: " + fails);
            var bytes = Encoding.UTF8.GetBytes(log.ToString());   // 창 프로그램이라 콘솔 인코딩을 못 바꾼다 — 바이트로 직접
            using (var o = Console.OpenStandardOutput()) o.Write(bytes, 0, bytes.Length);
            return fails == 0 ? 0 : 1;
        }

        // ---------- 통신 ----------
        static void Address()
        {
            char a; int w, b;
            XgtProtocol.WordAddr("D401", out a, out w); Check(a == 'D' && w == 401, "D401");
            XgtProtocol.BitAddr("M382", out a, out w, out b); Check(a == 'M' && w == 38 && b == 2, "M382");
            XgtProtocol.BitAddr("M118", out a, out w, out b); Check(w == 11 && b == 8, "M118");
            XgtProtocol.BitAddr("P42", out a, out w, out b); Check(a == 'P' && w == 4 && b == 2, "P42");
            XgtProtocol.BitAddr("M38F", out a, out w, out b); Check(w == 38 && b == 15, "M38F");
            Throws<FormatException>(() => XgtProtocol.WordAddr("%DW1", out a, out w), "%DW1 거부");
        }

        static void Frame()
        {
            var f = XgtProtocol.BuildReadRequest('D', 400, 2, 7);
            var name = Encoding.ASCII.GetBytes("%DB800");
            var body = new List<byte>();
            foreach (int v in new[] { 0x54, 0x14, 0, 1, name.Length }) XgtProtocol.Put16(body, v);
            body.AddRange(name);
            XgtProtocol.Put16(body, 4);
            Check(Encoding.ASCII.GetString(f, 0, 10) == "LSIS-XGT\0\0", "Company ID");
            Check(f[13] == 0x33, "source");
            Check(XgtProtocol.U16(f, 14) == 7 && XgtProtocol.U16(f, 16) == body.Count, "invoke·length");
            Check(f[19] == (byte)f.Take(19).Sum(x => x), "BCC");
            Check(f.Skip(20).SequenceEqual(body), "본문");
        }

        static void ErrorAndLimit()
        {
            var body = new List<byte>();
            foreach (int v in new[] { 0x55, 0x14, 0, 0xFFFF, 0x0012 }) XgtProtocol.Put16(body, v);
            try { XgtProtocol.ParseReadResponse(body.ToArray()); Check(false, "오류 응답"); }
            catch (XgtError e) { Check(e.Message.Contains("0x0012"), "오류 응답 코드 표시"); }
            Throws<ArgumentException>(() => XgtProtocol.BuildReadRequest('D', 0, 701, 1), "701워드 거부");
        }

        // 절대 규칙 1: 쓰기 함수·쓰기 명령 상수가 없어야 한다
        static void NoWrite()
        {
            var all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (var t in new[] { typeof(XgtProtocol), typeof(XgtClient) })
            {
                Check(!t.GetMembers(all).Any(m => m.Name.ToLowerInvariant().Contains("write")), t.Name + " 에 write 이름 없음");
                Check(!t.GetFields(all).Where(fi => fi.IsStatic).Any(fi => { var v = fi.GetValue(null); return v is int && (int)v == 0x58 || v is byte && (byte)v == 0x58; }),
                    t.Name + " 에 0x58 상수 없음");
            }
        }

        static void SimRoundTrip()
        {
            var plc = new FakePlc(0);
            try
            {
                plc.SetWord("D401", 25);
                plc.SetWord("D402", -15);
                plc.SetBit("M382", true);
                var c = new XgtClient("127.0.0.1", plc.Port, 2);
                try
                {
                    Check(c.ReadWords('D', 401, 2).SequenceEqual(new[] { 25, 0xFFF1 }), "D401~402");
                    Check(c.ReadBit("M382") == 1 && c.ReadBit("M383") == 0, "비트");
                    Check(c.ReadWord("D5190") == 0, "빈 주소 0");
                }
                finally { c.Close(); }

                // 시뮬레이터가 읽기 외 명령(0x0058)을 오류로 돌려주는지
                var name = Encoding.ASCII.GetBytes("%DB0");
                var body = new List<byte>();
                foreach (int v in new[] { 0x58, 0x14, 0, 1, name.Length }) XgtProtocol.Put16(body, v);
                body.AddRange(name);
                XgtProtocol.Put16(body, 2); XgtProtocol.Put16(body, 0);
                using (var s = new TcpClient("127.0.0.1", plc.Port))
                {
                    var ns = s.GetStream();
                    var frame = XgtProtocol.MakeHeader(body.Count, 1, XgtProtocol.SrcClient, 0).Concat(body).ToArray();
                    ns.Write(frame, 0, frame.Length);
                    var head = new byte[20];
                    int got = 0;
                    while (got < 20) got += ns.Read(head, got, 20 - got);
                    int src, inv, n;
                    XgtProtocol.ParseHeader(head, out src, out inv, out n);
                    var resp = new byte[n];
                    got = 0;
                    while (got < n) got += ns.Read(resp, got, n - got);
                    Check(XgtProtocol.U16(resp, 6) == 0xFFFF, "쓰기 명령 오류 응답");
                }
                Check(plc.Peek('D', 0, 2).All(x => x == 0), "메모리 그대로");
            }
            finally { plc.Stop(); }
        }

        // ---------- 태그 ----------
        static void SuggestScale()
        {
            Check(Tags.SuggestScale(25, 0.25, 0) == 0.01, "25 → 0.25");
            Check(Tags.SuggestScale(-15, -0.15, 0) == 0.01, "-15 → -0.15");
            Check(Tags.SuggestScale(500, 30, -20) == 0.1, "오프셋");
            Check(Tags.SuggestScale(0, 1.0, 0) == null, "원시값 0");
        }

        static void SaveTags()
        {
            var rows = Csv.Read(TagsPath());
            string[] header = rows[0];
            var body = rows.Skip(1).Select(r => (string[])r.Clone()).ToList();
            string path = Path.Combine(NewDir(), "tags.csv");
            int sc = Array.IndexOf(header, "배율");
            body[0][sc] = "0.1";
            Tags.Save(path, header, body);
            Check(Tags.Load(path)[0].Scale == 0.1, "저장 후 다시 읽기");
            Check(Tags.Load(path).Count == rows.Count - 1 && Tags.Load(path).Any(t => t.Note.Contains(",")), "쉼표 든 비고 유지");
            var before = File.ReadAllBytes(path);
            body[0][sc] = "abc";
            Throws<FormatException>(() => Tags.Save(path, header, body), "잘못된 배율 거부");
            Check(File.ReadAllBytes(path).SequenceEqual(before), "실패하면 원래 파일 유지");
            Check(!File.Exists(Path.ChangeExtension(path, ".tmp")), "임시 파일 지움");
        }

        static void Blocks()
        {
            var tags = Tags.Load(TagsPath());
            var blocks = Tags.PlanBlocks(tags);
            var covered = new HashSet<string>(blocks.SelectMany(b => Enumerable.Range(b.Start, b.Count).Select(i => b.Area + ":" + i)));
            Check(tags.All(t => covered.Contains(t.Area + ":" + t.Word) && (t.Type != "INT32" || covered.Contains(t.Area + ":" + (t.Word + 1)))), "모든 태그 포함");
            Check(blocks.All(b => b.Count <= 700), "700워드 이하");
            Check(blocks.Any(b => b.Area == 'D' && b.Start == 3185 && b.Count == 6), "진공도는 따로 묶임");
        }

        static void Decode()
        {
            var tags = Tags.Load(TagsPath()).Where(t => t.Key == "p_lower" || t.Key == "pos_fb" || t.Key == "rsp_remote").ToList();
            var blocks = new List<Block> { new Block { Area = 'D', Start = 380, Count = 23 }, new Block { Area = 'M', Start = 38, Count = 1 } };
            var d = new int[23];
            d[0] = 0xFFFE; d[1] = 0xFFFF;   // D380~381 = -2 (INT32)
            d[22] = (-15) & 0xFFFF;         // D402
            Dictionary<string, double> v;
            Dictionary<string, long> raw;
            Tags.Decode(tags, blocks, new List<int[]> { d, new[] { 4 } }, out v, out raw);
            Check(Near(v["p_lower"], -0.15) && raw["p_lower"] == -15, "INT16 음수");
            Check(v["pos_fb"] == -2, "INT32 음수");
            Check(v["rsp_remote"] == 1, "비트");
        }

        static void Derived()
        {
            Func<Dictionary<string, double>> mk = () => new Dictionary<string, double>
                { { "wheel_rpm", 1500 }, { "p_upper", 0.5 }, { "p_lower", 0.1 }, { "vg1_mant", 3.5 }, { "vg1_exp", -3 }, { "vg2_mant", 1.2 }, { "vg2_exp", -2 } };
            var v = mk();
            Calc.AddDerived(v, 250);
            Check(Near(v["wheel_v"], 19.635, 1e-3), "휠 선속도");
            Check(Near(v["dp"], 0.4), "차압");
            Check(Near(v["vg1"], 3.5e-3), "진공도");
            v = mk();
            Calc.AddDerived(v, 0);
            Check(!v.ContainsKey("wheel_v"), "지름 0이면 선속도 없음");
        }

        static void Poll()
        {
            var tags = Tags.Load(TagsPath());
            var got = new List<Dictionary<string, double>>();
            var status = new List<bool>();
            var plc = new FakePlc(0);
            plc.SetWord("D401", 25);
            var poller = new Poller(new XgtClient("127.0.0.1", plc.Port, 0.5), tags, 0.1,
                (ts, v, r) => { lock (got) got.Add(v); }, (ok, m) => { lock (status) status.Add(ok); });
            poller.Start();
            for (int i = 0; i < 50; i++) { lock (got) if (got.Count >= 3) break; Thread.Sleep(100); }
            poller.Stop();
            plc.Stop();
            Check(got.Count >= 3, "3번 이상 읽음");
            Check(got.Count > 0 && Near(got[0]["p_upper"], 0.25), "환산값");
            Check(status.Count > 0 && status[status.Count - 1], "연결됨");
        }

        static void Backoff()
        {
            var status = new List<KeyValuePair<bool, string>>();
            var poller = new Poller(new XgtClient("127.0.0.1", 1, 0.2), Tags.Load(TagsPath()), 1,
                (ts, v, r) => { }, (ok, m) => { lock (status) status.Add(new KeyValuePair<bool, string>(ok, m)); });
            poller.Start();
            Thread.Sleep(600);
            poller.Stop();
            Check(status.Count > 0 && !status[0].Key && status[0].Value.Contains("1초 뒤 재접속"), "첫 실패 뒤 1초 대기");
        }

        // ---------- 기록 ----------
        const double T = 1800000000.0;

        static List<Dictionary<string, string>> Series(string p)
        {
            var rows = Csv.Read(p);
            int hi = rows.FindIndex(x => x[0] == "시각");
            var h = rows[hi];
            return rows.Skip(hi + 1).Where(x => !x[0].StartsWith("#"))
                .Select(x => { var d = new Dictionary<string, string>(); for (int i = 0; i < h.Length && i < x.Length; i++) d[h[i]] = x[i]; return d; }).ToList();
        }

        static Recorder NewRecorder(string dir) { return new Recorder(dir, Tags.Load(TagsPath())); }

        static void EjectionRecording()
        {
            string dir = NewDir();
            var r = NewRecorder(dir);
            string p = r.Start(T, new Dictionary<string, string> { { "시료명", "Fe/Si B:1" } });
            int? second = null;
            for (int i = 1; i <= 300; i++)   // 0.2초 간격 60초
            {
                double t = T + i * 0.2;
                bool ej = 20 <= i * 0.2;
                var v = new Dictionary<string, double>
                {
                    { "p_upper", ej ? 0.5 : 0.2 }, { "stopper_up", ej ? 1 : 0 },
                    { "t_wheel_out", 20 <= i * 0.2 && i * 0.2 <= 25 ? 30.0 : 22.0 }, { "dp", 0.4 },
                };
                r.Add(t, v, new Dictionary<string, long> { { "p_upper", (long)(v["p_upper"] * 100) } });
                if (i == 125) r.EjectEnd(t);                       // 25초 수동 종료
                if (i == 150) r.Event(t, "연결 끊김");
                if (i == 285) second = r.EjectStart(t);            // 57초 수동 시작
                if (i == 290) r.EjectEnd(t);
            }
            string p2 = r.Stop(T + 60, new Dictionary<string, string> { { "외관", "양호" } });
            Check(second == 2, "두 번째 출탕 번호");
            Check(p == p2, "같은 파일");
            Check(p.EndsWith("_Fe_Si_B_1.csv"), "파일 이름 " + Path.GetFileName(p));
            var s = Series(p);
            var data = s.Where(x => x["이벤트"] == "").ToList();
            var ts = data.Select(x => double.Parse(x["경과초"], App.Inv)).ToList();
            Check(ts.SequenceEqual(ts.OrderBy(x => x)), "시간 순서");
            // 출탕 창 [10, 55] 안은 0.2초 전부, 밖은 약 1초 간격
            Check(ts.Count(t => 10 <= t && t <= 55) == 226, "출탕 창 안 226점 (" + ts.Count(t => 10 <= t && t <= 55) + ")");
            var outside = ts.Where(t => t < 10).ToList();
            Check(outside.Zip(outside.Skip(1), (a, b) => b - a).All(d => 0.9 < d && d < 1.1), "창 밖 1초 간격");
            var no1 = data.Where(x => x["출탕구간번호"] == "1").Select(x => double.Parse(x["경과초"], App.Inv)).ToList();
            Check(no1.First() == 20.0 && no1.Last() == 25.0, "출탕 1 구간 20~25초");
            Check(data.Any(x => x["출탕구간번호"] == "2"), "출탕 2 있음");
            var evs = s.Where(x => x["이벤트"] != "").Select(x => x["이벤트"]).ToList();
            Check(evs.SequenceEqual(new[] { "Run 시작", "출탕 1 시작(자동(M151))", "출탕 1 종료", "연결 끊김", "출탕 2 시작(수동)", "출탕 2 종료", "Run 정지" }),
                "이벤트 순서: " + string.Join(" / ", evs));

            var all = Csv.Read(p);
            Check(all.Any(x => x.Length == 2 && x[0] == "# 외관" && x[1] == "양호"), "결과 입력 저장");
            int hi = all.FindIndex(x => x[0] == "# 출탕 요약");
            var first = all[hi].Skip(1).Zip(all[hi + 1].Skip(1), (k, v) => new { k, v }).ToDictionary(x => x.k, x => x.v);
            Check(first["출탕 시간(s)"] == "5.0", "출탕 시간");
            Check(first["상부 챔버 압력(PG1) 시작"] == "0.5", "시작값");
            Check(first["휠 출수온도 상승폭"] == "8", "출수온도 상승폭");

            var idx = Csv.Read(Path.Combine(dir, "run_index.csv"));
            Check(idx.Count == 3, "run_index 머리줄 + 출탕 2줄");
            Check(idx[2][Array.IndexOf(idx[0], "출탕번호")] == "2", "run_index 출탕번호");

            List<KeyValuePair<string, string>> meta;
            string[] hdr;
            List<Dictionary<string, string>> rows;
            HistoryForm.LoadRun(p, out meta, out hdr, out rows);
            Check(meta.Any(m => m.Key == "시료명" && m.Value == "Fe/Si B:1"), "지난 기록 메타");
            Check(hdr.Contains("출탕구간번호") && rows.Count == s.Count, "지난 기록 행");

            var raw = Series(Path.Combine(dir, Path.GetFileNameWithoutExtension(p) + "_raw.csv"));
            Check(raw[1]["p_upper"] == "20", "원시값 파일");

            // 화면 두 개가 이 파일·기본 tags.csv 로 오류 없이 만들어지는지 (띄우지는 않음)
            using (var h = new HistoryForm(p)) Check(h.Text.Contains(Path.GetFileName(p)), "지난 기록 창 만들기");
            using (var te = new TagEditorForm(TagsPath(), () => { }))
            {
                te.SetRaws(new Dictionary<string, long> { { "p_upper", 25 } });
                Check(true, "태그 설정 창 만들기·원시값 표시");
            }
            string st = Path.Combine(dir, "settings.toml");
            File.WriteAllBytes(st, App.DefaultBytes("settings.toml"));
            using (var f = new PlcSettingsForm(st)) Check(f.Controls.Count > 0, "PLC 접속 설정 창 만들기");
        }

        static void NoEjectionDupName()
        {
            string dir = NewDir();
            var r = NewRecorder(dir);
            r.Start(T, new Dictionary<string, string> { { "시료명", "A" } });
            r.Add(T + 1, new Dictionary<string, double> { { "stopper_up", 1 } }, new Dictionary<string, long>());   // 처음부터 켜져 있으면 상승이 아님
            Check(r.Ejections.Count == 0, "처음부터 켜짐은 출탕 아님");
            string p1 = r.Stop(T + 2);
            r.Start(T, new Dictionary<string, string> { { "시료명", "A" } });
            string p2 = r.Stop(T + 1);
            Check(p2.EndsWith("_A_2.csv") && p1 != p2, "같은 이름이면 _2");
            Check(Csv.Read(Path.Combine(dir, "run_index.csv")).Count == 3, "출탕 없으면 Run 한 줄");
        }

        static void IndexHeaderChange()
        {
            string dir = NewDir();
            string old = Path.Combine(dir, "run_index.csv");
            File.WriteAllText(old, "파일,옛열\r\nx,y\r\n", new UTF8Encoding(true));
            var r = NewRecorder(dir);
            r.Start(T, new Dictionary<string, string> { { "시료명", "A" } });
            r.Stop(T + 1);
            Check(Directory.GetFiles(dir, "run_index_이전_*.csv").Length == 1, "옛 파일 보존");
            var rows = Csv.Read(old);
            Check(rows[0][0] == "파일" && rows.Count == 2 && rows[0].Length > 2, "새 머리줄로 다시 씀");
        }

        // ---------- 업데이트·설정 ----------
        static void VersionCompare()
        {
            Check(Updater.IsNewer("0.3.0", "0.2.0") && Updater.IsNewer("v0.2.1", "0.2.0") && Updater.IsNewer("1.0", "0.9.9"), "새 버전");
            Check(!Updater.IsNewer("0.2.0", "0.2.0") && !Updater.IsNewer("0.1.9", "0.2.0"), "같거나 옛 버전");
        }

        static void Sha()
        {
            string h = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";   // sha256("abc")
            var abc = Encoding.ASCII.GetBytes("abc");
            Check(Updater.Verified(abc, h + "  RSP_Logger.exe\n"), "맞는 해시");
            Check(!Updater.Verified(Encoding.ASCII.GetBytes("abd"), h) && !Updater.Verified(abc, ""), "틀린 해시·빈 값");
        }

        static void SettingsParse()
        {
            var s = Settings.Parse(Encoding.UTF8.GetString(App.DefaultBytes("settings.toml")).TrimStart('\uFEFF'));
            Check(s.Str("plc.host") == "192.168.1.2" && s.Num("plc.port") == 2004, "[plc]");
            Check(s.Num("ejection.fast_interval_s") == 0.2 && s.Str("record.dir") == "", "주석 붙은 값·빈 문자열");
            var t = Settings.Parse("[record]\ndir = \"D:\\\\RSP # 기록\"  # 주석\n");
            Check(t.Str("record.dir") == "D:\\RSP # 기록", "경로·따옴표 안 #");
            Check(NetCheck.SubnetHint("127.0.0.1") == "" && NetCheck.SubnetHint("203.0.113.5") != "", "PC IP 대역 검사");
            string toml = "[plc]\r\nhost = \"192.168.1.2\"  # PLC\r\nport = 2004\r\n[x]\r\nport = 1\r\n";
            var u = Settings.Parse(Settings.SetValue(Settings.SetValue(toml, "plc", "host", "\"10.0.0.7\""), "plc", "port", "502"));
            Check(u.Str("plc.host") == "10.0.0.7" && u.Num("plc.port") == 502 && u.Num("x.port") == 1
                && Settings.SetValue(toml, "plc", "host", "\"a\"").Contains("# PLC\r\n"), "설정 값 바꾸기(주석·다른 구역 유지)");
            int pp; double tt;
            Check(PlcSettingsForm.Check("192.168.1.2", "2004", "1", out pp, out tt) == null && pp == 2004
                && PlcSettingsForm.Check("192.168.1", "2004", "1", out pp, out tt) != null
                && PlcSettingsForm.Check("192.168.1.2", "70000", "1", out pp, out tt) != null, "PLC 접속 설정 입력 검사");
            Check(NetCheck.SameNet("192.168.1.50", "192.168.1.2", "255.255.255.0") && !NetCheck.SameNet("192.168.2.50", "192.168.1.2", "255.255.255.0")
                && PlcSettingsForm.CheckPcIp("192.168.1.50", "255.255.255.0", "192.168.1.2") == null
                && PlcSettingsForm.CheckPcIp("192.168.1.2", "255.255.255.0", "192.168.1.2") != null
                && PlcSettingsForm.CheckPcIp("192.168.2.50", "255.255.255.0", "192.168.1.2") != null
                && PlcSettingsForm.CheckPcIp("192.168.1", "255.255.255.0", "192.168.1.2") != null, "노트북 IP 입력 검사");
            Check(NetCheck.NetshStatic("이더넷", "192.168.1.50", "255.255.255.0")
                == "interface ipv4 set address name=\"이더넷\" source=static address=192.168.1.50 mask=255.255.255.0 gateway=none"
                && NetCheck.NetshDhcp("이더넷") == "interface ipv4 set address name=\"이더넷\" source=dhcp", "netsh 인수");
        }
    }
}

// LS XGT 전용 프로토콜(FEnet, TCP 2004) — 읽기 전용.
// 쓰기(0x0058)·RUN/STOP 등 쓰기·제어 명령은 만들지 않는다 (CLAUDE.md 절대 규칙 1).
// 프레임 형식은 LS FEnet 매뉴얼 "XGT 전용 프로토콜" 기준. 현장 첫 접속 때 XG5000 모니터 값과 대조할 것.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RspLogger
{
    class XgtError : Exception
    {
        public XgtError(string m) : base(m) { }
    }

    static class XgtProtocol
    {
        public static readonly byte[] CompanyId = Encoding.ASCII.GetBytes("LSIS-XGT\0\0");
        public const byte SrcClient = 0x33;
        public const byte SrcServer = 0x11;
        public const int CmdReadReq = 0x0054;
        public const int CmdReadResp = 0x0055;
        public const int TypeContinuous = 0x0014;   // 바이트 단위 연속 읽기
        public const int MaxReadBytes = 1400;
        // Company ID(10), PLC Info(2), CPU Info(1), Source(1), Invoke ID(2), Length(2), FEnet 위치(1), BCC(1)
        public const int HeaderSize = 20;

        // "D401" -> D, 401
        public static void WordAddr(string text, out char area, out int word)
        {
            var m = Regex.Match(text.Trim().ToUpperInvariant(), @"^([A-Z])([0-9]+)\z");
            if (!m.Success) throw new FormatException("워드 주소 형식 오류: " + text);
            area = m.Groups[1].Value[0];
            word = int.Parse(m.Groups[2].Value, App.Inv);
        }

        // "M382" -> M, 38, 2. 마지막 한 자리는 16진 비트 번호, 앞은 워드 번호.
        public static void BitAddr(string text, out char area, out int word, out int bit)
        {
            var m = Regex.Match(text.Trim().ToUpperInvariant(), @"^([A-Z])([0-9]*)([0-9A-F])\z");
            if (!m.Success) throw new FormatException("비트 주소 형식 오류: " + text);
            area = m.Groups[1].Value[0];
            word = m.Groups[2].Value == "" ? 0 : int.Parse(m.Groups[2].Value, App.Inv);
            bit = Convert.ToInt32(m.Groups[3].Value, 16);
        }

        public static int U16(byte[] d, int o) { return d[o] | (d[o + 1] << 8); }

        public static void Put16(List<byte> b, int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }

        public static byte[] MakeHeader(int length, int invoke, byte source, byte cpuInfo)
        {
            var h = new byte[HeaderSize];
            Array.Copy(CompanyId, h, 10);
            h[12] = cpuInfo;
            h[13] = source;
            h[14] = (byte)invoke; h[15] = (byte)(invoke >> 8);
            h[16] = (byte)length; h[17] = (byte)(length >> 8);
            int sum = 0;
            for (int i = 0; i < 19; i++) sum += h[i];
            h[19] = (byte)sum;
            return h;
        }

        public static void ParseHeader(byte[] d, out int source, out int invoke, out int length)
        {
            if (Encoding.ASCII.GetString(d, 0, 8) != "LSIS-XGT")
                throw new XgtError("XGT 헤더가 아님: " + BitConverter.ToString(d, 0, 10));
            source = d[13];
            invoke = U16(d, 14);
            length = U16(d, 16);
        }

        // area 영역의 startWord부터 count 워드를 한 번에 읽는 요청 프레임
        public static byte[] BuildReadRequest(char area, int startWord, int count, int invoke)
        {
            int nbytes = count * 2;
            if (nbytes <= 0 || nbytes > MaxReadBytes)
                throw new ArgumentException("한 번에 읽을 수 있는 워드 수는 1~" + MaxReadBytes / 2 + ": " + count);
            byte[] name = Encoding.ASCII.GetBytes("%" + area + "B" + (startWord * 2).ToString(App.Inv));
            var body = new List<byte>();
            foreach (int v in new[] { CmdReadReq, TypeContinuous, 0, 1, name.Length }) Put16(body, v);
            body.AddRange(name);
            Put16(body, nbytes);
            return MakeHeader(body.Count, invoke, SrcClient, 0).Concat(body).ToArray();
        }

        // 읽기 응답 본문 -> 부호 없는 16비트 워드 목록
        public static int[] ParseReadResponse(byte[] body)
        {
            if (body.Length < 10) throw new XgtError("응답이 너무 짧음");
            int cmd = U16(body, 0), err = U16(body, 6), code = U16(body, 8);
            if (cmd != CmdReadResp) throw new XgtError(string.Format("읽기 응답이 아님: 명령 0x{0:X4}", cmd));
            if (err != 0) throw new XgtError(string.Format("PLC 오류 응답: 코드 0x{0:X4}", code));
            if (body.Length < 12) throw new XgtError("응답이 너무 짧음");
            int n = U16(body, 10), avail = body.Length - 12;
            if (avail < n || n % 2 != 0)
                throw new XgtError(string.Format("데이터 길이 이상: {0}바이트 예상, {1}바이트 받음", n, Math.Min(avail, n)));
            var w = new int[n / 2];
            for (int i = 0; i < w.Length; i++) w[i] = U16(body, 12 + i * 2);
            return w;
        }
    }

    // XGT 클라이언트 — 소켓 1개, 읽기만
    class XgtClient
    {
        readonly string host;
        readonly int port, timeoutMs;
        TcpClient tcp;
        NetworkStream ns;
        int invoke;

        public XgtClient(string host, int port, double timeoutS)
        {
            this.host = host; this.port = port; timeoutMs = (int)(timeoutS * 1000);
        }

        public void Connect()
        {
            Close();
            var c = new TcpClient();
            try
            {
                var ar = c.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) throw new SocketException(10060);   // 시간 초과
                c.EndConnect(ar);
            }
            catch { c.Close(); throw; }
            c.ReceiveTimeout = c.SendTimeout = timeoutMs;
            c.NoDelay = true;
            tcp = c;
            ns = c.GetStream();
        }

        public void Close()
        {
            if (tcp == null) return;
            try { tcp.Close(); }
            finally { tcp = null; ns = null; }
        }

        byte[] RecvExact(int n)
        {
            var b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = ns.Read(b, got, n - got);
                if (r == 0) throw new IOException("PLC가 연결을 끊음");
                got += r;
            }
            return b;
        }

        // 끊기면 소켓을 닫고 예외를 올린다. 재접속은 호출하는 쪽(Poller)이 한다.
        public int[] ReadWords(char area, int start, int count)
        {
            if (tcp == null) Connect();
            invoke = (invoke + 1) & 0xFFFF;
            int source, inv, length;
            byte[] body;
            try
            {
                var req = XgtProtocol.BuildReadRequest(area, start, count, invoke);
                ns.Write(req, 0, req.Length);
                XgtProtocol.ParseHeader(RecvExact(XgtProtocol.HeaderSize), out source, out inv, out length);
                body = RecvExact(length);
            }
            catch { Close(); throw; }
            if (source != XgtProtocol.SrcServer || inv != invoke)
            {
                Close();   // 응답이 어긋나면 다음 요청과 섞이지 않게 새로 연결
                throw new XgtError(string.Format("응답 불일치: source 0x{0:X2}, invoke {1}≠{2}", source, inv, invoke));
            }
            var words = XgtProtocol.ParseReadResponse(body);
            if (words.Length != count) throw new XgtError(count + "워드 요청, " + words.Length + "워드 받음");
            return words;
        }

        public int ReadWord(string addr)
        {
            char a; int w;
            XgtProtocol.WordAddr(addr, out a, out w);
            return ReadWords(a, w, 1)[0];
        }

        public int ReadBit(string addr)
        {
            char a; int w, b;
            XgtProtocol.BitAddr(addr, out a, out w, out b);
            return (ReadWords(a, w, 1)[0] >> b) & 1;
        }
    }

    // 가상 PLC — XGT 연속 읽기(0x0054/0x0014)에만 응답하는 TCP 서버. 다른 명령은 오류로 돌려준다.
    class FakePlc
    {
        const int ErrUnsupported = 0x0003;   // 시뮬레이터용 임의 코드
        readonly TcpListener listener;
        readonly Dictionary<char, byte[]> mem = new Dictionary<char, byte[]>();
        readonly object lk = new object();
        volatile bool stopped;

        public FakePlc(int port)
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            new Thread(AcceptLoop) { IsBackground = true }.Start();
        }

        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }

        public void Stop() { stopped = true; listener.Stop(); }

        void AcceptLoop()
        {
            while (!stopped)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { return; }
                new Thread(() => Handle(c)) { IsBackground = true }.Start();
            }
        }

        static byte[] RecvExact(Stream s, int n)
        {
            var b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = s.Read(b, got, n - got);
                if (r == 0) return null;
                got += r;
            }
            return b;
        }

        void Handle(TcpClient c)
        {
            using (c)
            {
                try
                {
                    var s = c.GetStream();
                    while (!stopped)
                    {
                        var head = RecvExact(s, XgtProtocol.HeaderSize);
                        if (head == null) return;
                        int src, inv, len;
                        XgtProtocol.ParseHeader(head, out src, out inv, out len);
                        var body = RecvExact(s, len);
                        if (body == null) return;
                        var outb = Respond(body);
                        var frame = XgtProtocol.MakeHeader(outb.Length, inv, XgtProtocol.SrcServer, 0xB0).Concat(outb).ToArray();
                        s.Write(frame, 0, frame.Length);
                    }
                }
                catch (Exception) { }   // 이상한 프레임·끊김이면 이 연결만 닫는다
            }
        }

        byte[] Mem(char area)
        {
            byte[] m;
            if (!mem.TryGetValue(area, out m)) mem[area] = m = new byte[0x20000];   // 영역별 바이트 메모리
            return m;
        }

        public byte[] Peek(char area, int start, int n)
        {
            lock (lk) return Mem(area).Skip(start).Take(n).ToArray();
        }

        public void SetWord(string addr, int value)
        {
            char a; int w;
            XgtProtocol.WordAddr(addr, out a, out w);
            lock (lk) { var m = Mem(a); m[w * 2] = (byte)value; m[w * 2 + 1] = (byte)(value >> 8); }
        }

        public void SetBit(string addr, bool on)
        {
            char a; int w, b;
            XgtProtocol.BitAddr(addr, out a, out w, out b);
            lock (lk)
            {
                var m = Mem(a);
                int v = XgtProtocol.U16(m, w * 2);
                v = on ? v | (1 << b) : v & ~(1 << b);
                m[w * 2] = (byte)v; m[w * 2 + 1] = (byte)(v >> 8);
            }
        }

        public byte[] Respond(byte[] body)
        {
            int cmd = XgtProtocol.U16(body, 0), dtype = XgtProtocol.U16(body, 2);
            int blocks = XgtProtocol.U16(body, 6), nlen = XgtProtocol.U16(body, 8);
            var r = new List<byte>();
            Match m = null;
            if (body.Length >= 12 + nlen)
                m = Regex.Match(Encoding.ASCII.GetString(body, 10, nlen), @"^%([A-Z])B([0-9]+)\z");
            if (cmd != XgtProtocol.CmdReadReq || dtype != XgtProtocol.TypeContinuous || blocks != 1 || m == null || !m.Success)
            {
                foreach (int v in new[] { cmd + 1, dtype, 0, 0xFFFF, ErrUnsupported }) XgtProtocol.Put16(r, v);
                return r.ToArray();
            }
            int n = XgtProtocol.U16(body, 10 + nlen);
            int start = int.Parse(m.Groups[2].Value, App.Inv);
            var data = Peek(m.Groups[1].Value[0], start, n);
            foreach (int v in new[] { XgtProtocol.CmdReadResp, dtype, 0, 0, 1, data.Length }) XgtProtocol.Put16(r, v);
            r.AddRange(data);
            return r.ToArray();
        }

        // 개발 모드용: 공정처럼 움직이는 원시값. cycle초마다 ejectS초 동안 출탕(M118·M151 ON, 상부 압력 상승).
        public void StartAnimation(double cycle, double ejectS)
        {
            new Thread(() =>
            {
                var rnd = new Random();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Func<double, double> n = a => (rnd.NextDouble() * 2 - 1) * a;
                while (!stopped)
                {
                    double t = sw.Elapsed.TotalSeconds;
                    bool ej = (t % cycle) > cycle - ejectS;
                    SetWord("D401", (int)((ej ? 50 : 20) + n(1)));
                    SetWord("D402", (int)n(1));
                    SetWord("D3185", (int)(350 + n(5)));
                    SetWord("D3190", -3);
                    SetWord("D5185", (int)(120 + n(3)));
                    SetWord("D5190", -2);
                    SetWord("D201", (int)(250 + n(2)));
                    SetWord("D202", (int)(240 + n(2)));
                    SetWord("D203", (int)(8000 + n(20)));
                    SetWord("D204", (int)((ej ? 300 : 220) + n(3)));
                    SetWord("D221", (int)(1400 + 20 * Math.Sin(t / 10) + n(2)));
                    SetWord("D385", 14000);
                    SetWord("D324", (int)(1500 + n(5)));
                    SetWord("D888", (int)(400 + n(3)));
                    SetWord("D892", (int)(15 + n(1)));
                    SetBit("M118", ej);
                    SetBit("M151", ej);
                    SetBit("M382", true);
                    Thread.Sleep(200);
                }
            }) { IsBackground = true }.Start();
        }
    }
}

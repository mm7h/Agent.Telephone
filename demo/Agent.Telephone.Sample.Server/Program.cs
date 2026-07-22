using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace XiaoZhi.Net.Telephone.Sample.Server
{
    /// <summary>
    /// 电话 SIP 服务端 POC
    ///
    /// 【HT701 网关配置 - FXS 端口设置】
    ///   主SIP服务器      → 运行本程序的电脑 IP（启动后控制台会打印）
    ///   SIP用户ID        → 1000（任意值）
    ///   摘机自动拨号     → 1000（与用户ID一致，摘机即自动呼叫服务端）
    ///   摘机自动拨号延迟 → 0
    ///   非注册拨打模式   → Yes
    ///   本地SIP端口      → 5060（默认）
    ///   本地RTP端口      → 5004（默认）
    /// </summary>
    internal class Program
    {
        private const int SIP_PORT = 5060;
        private const int SAMPLE_RATE_HZ = 8000; // G.711 固定采样率

        // 最近一次注册成功的设备端点（线程安全：只在注册时写，外呼时读）
        private static volatile SIPEndPoint? _deviceEndPoint;

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            string localIP = GetLocalIP();

            Console.WriteLine("=== 电话 SIP 服务端 (POC Demo) ===");
            Console.WriteLine($"SIP 监听端口: UDP {SIP_PORT}");
            Console.WriteLine($"本机 IP 地址: {localIP}");
            Console.WriteLine();
            Console.WriteLine("【HT701 网关 → FXS端口 配置说明】");
            Console.WriteLine($"  主SIP服务器      = {localIP}");
            Console.WriteLine("  摘机自动拨号     = 1000");
            Console.WriteLine("  摘机自动拨号延迟 = 0");
            Console.WriteLine("  非注册拨打模式   = Yes");
            Console.WriteLine();
            Console.WriteLine("等待来电...");
            Console.WriteLine("按 [O] 发起外呼，按 Ctrl+C 退出\n");

            var sipTransport = new SIPTransport();
            sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, SIP_PORT)));

            // 处理 REGISTER 请求：直接回 200 OK，让 HT701 注册成功。
            // 这样 HT701 的"摘机自动拨号"功能才能可靠触发（该功能依赖注册状态）。
            sipTransport.SIPTransportRequestReceived += async (localEP, remoteEP, req) =>
            {
                if (req.Method == SIPMethodsEnum.REGISTER)
                {
                    _deviceEndPoint = remoteEP; // 保存设备地址，供外呼使用
                    var resp = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null);
                    resp.Header.Contact = req.Header.Contact;
                    if (resp.Header.Contact?.Count > 0)
                        resp.Header.Contact[0].Expires = req.Header.Expires > 0 ? req.Header.Expires : 60;
                    await sipTransport.SendResponseAsync(resp);
                    Console.WriteLine($"[{Ts}] 设备注册成功: {remoteEP}");
                }
                Console.WriteLine($"CallId: {req.Header.CallId}, FromURI: {req.Header.From.FromURI.User}, ToURI: {req.Header.To.ToURI.User}");
                Console.WriteLine($"Method: {req.Method}");
            };

            // isServerAgent=true：将传输层收到的所有 SIP 请求路由到此 UA
            var ua = new SIPUserAgent(sipTransport, null, true);

            ua.ServerCallCancelled += (uas, cancelReq) =>
                Console.WriteLine($"[{Ts}] 来电已取消（摘机前挂断）");

            int callCount = 0;
            ua.OnIncomingCall += async (agent, req) =>
            {
                
                int id = Interlocked.Increment(ref callCount);
                string caller = req.Header.From?.FromName?.Trim()
                             ?? req.Header.From?.FromURI?.User
                             ?? "未知";
                Console.WriteLine($"[{Ts}] ★ 来电 #{id}，来自: {caller}");

                // 缓冲原始 G.711 载荷字节（每个 RTP 包 20ms = 160 字节）
                var buf = new List<byte>(160 * 600); // 预分配约 75 秒
                int detectedPayloadType = 0;          // 默认 PCMU

                // VoIPMediaSession 无需音频设备，可纯粹用于 RTP 收发
                var session = new VoIPMediaSession();
                session.AcceptRtpFromAny = true;

                // 订阅原始 RTP 包（VoIPMediaSession 继承自 RTPSession）
                session.OnRtpPacketReceived += (ep, mediaType, pkt) =>
                {
                    if (mediaType == SDPMediaTypesEnum.audio)
                    {
                        detectedPayloadType = pkt.Header.PayloadType;
                        lock (buf) buf.AddRange(pkt.Payload);
                    }
                };

                var uas = agent.AcceptCall(req);
                bool ok = await agent.Answer(uas, session);

                if (!ok)
                {
                    Console.WriteLine($"[{Ts}] 通话 #{id} 接听失败");
                    session.Close("answer failed");
                    return;
                }
                Console.WriteLine(ua.Dialogue.Id);
                // 关闭默认下行音源，避免话机听到背景音乐。
                try
                {
                    session.AudioExtrasSource.SetSource(AudioSourcesEnum.None);
                }
                catch
                {
                    // 某些实现不支持 None 时降级为静音。
                    session.AudioExtrasSource.SetSource(AudioSourcesEnum.Silence);
                }

                Console.WriteLine($"[{Ts}] 通话 #{id} 已接通，正在录音...");

                // 对方挂断（发送 BYE）时触发
                Action<SIPDialogue>? onHangup = null;
                onHangup = (dlg) =>
                {
                    agent.OnCallHungup -= onHangup;
                    Console.WriteLine($"[{Ts}] 通话 #{id} 已挂断");

                    byte[] captured;
                    lock (buf) { captured = buf.ToArray(); }

                    SaveWav(captured, id, detectedPayloadType);
                    session.Close("bye");
                };
                agent.OnCallHungup += onHangup;
            };

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            // 后台任务：监听按键，按 O 发起外呼
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    if (!Console.KeyAvailable) { await Task.Delay(100); continue; }
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key != ConsoleKey.O) continue;

                    if (_deviceEndPoint == null)
                    {
                        Console.WriteLine($"\n[{Ts}] 尚未收到设备注册，无法外呼");
                        continue;
                    }

                    Console.Write("\n请输入来电显示号码（仅数字，直接回车使用默认 10086）: ");
                    string? callerNumber = Console.ReadLine()?.Trim();
                    if (string.IsNullOrEmpty(callerNumber)) callerNumber = "10086";

                    await MakeOutboundCallAsync(ua, localIP, _deviceEndPoint, callerNumber);
                }
            }, cts.Token);

            try { await Task.Delay(Timeout.Infinite, cts.Token); }
            catch (TaskCanceledException) { }

            Console.WriteLine("\n正在关闭 SIP 服务端...");
            sipTransport.Shutdown();
        }

        /// <summary>
        /// 向已注册设备发起外呼，并设置任意来电显示名称。
        /// 来电显示通过 SIP From 头的 display-name 字段传递给 HT701，
        /// HT701 再经 FSK 信令将其送至模拟话机显示屏。
        /// 注意：老式模拟话机通常只支持 ASCII，中文字符可能无法正常显示。
        /// </summary>
        private static async Task MakeOutboundCallAsync(
            SIPUserAgent ua,
            string localIP,
            SIPEndPoint deviceEndPoint,
            string callerNumber)
        {
            Console.WriteLine($"[{Ts}] 正在向 {deviceEndPoint.Address}:{deviceEndPoint.Port} 外呼，来电号码: {callerNumber}");

            // 目标 URI：呼叫 HT701 上的分机 1000
            string destUri = $"sip:1000@{deviceEndPoint.Address}:{deviceEndPoint.Port}";
            var callDescriptor = new SIPCallDescriptor(destUri, sdp: null);

            // 来电显示原理：
            //   HT701 向模拟话机发 FSK 信令时，取的是 From URI 的【用户名部分】作为来电号码，
            //   而不是双引号里的 display-name。
            //   因此 fromUsername 必须是纯数字，否则 HT701 无法解析，会显示 "----0----"。
            //   最终 From 头形如: "10086" <sip:10086@192.168.x.x>
            callDescriptor.SetGeneralFromHeaderFields(
                fromDisplayName: callerNumber,   // display-name（可选，部分 IP 话机使用）
                fromUsername: callerNumber,   // ← 关键：这是 FSK 来电号码的来源
                fromHost: localIP);

            var session = new VoIPMediaSession();
            session.AcceptRtpFromAny = true;

            bool answered = await ua.Call(callDescriptor, session);

            if (!answered)
            {
                Console.WriteLine($"[{Ts}] 外呼未接听（被拒绝或超时）");
                session.Close("not answered");
                return;
            }

            // 接通后关闭下行音源，话机端不会听到背景音乐
            try { session.AudioExtrasSource.SetSource(AudioSourcesEnum.None); }
            catch { session.AudioExtrasSource.SetSource(AudioSourcesEnum.Silence); }

            Console.WriteLine($"[{Ts}] 外呼已接通，按 [H] 键挂断");

            // 等待用户按 H 主动挂断，或对方先挂机
            var hangupTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            ua.OnCallHungup += (dlg) => hangupTcs.TrySetResult("remote");

            _ = Task.Run(async () =>
            {
                while (!hangupTcs.Task.IsCompleted)
                {
                    if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.H)
                        hangupTcs.TrySetResult("local");
                    await Task.Delay(100);
                }
            });

            string reason = await hangupTcs.Task;
            if (reason == "local")
            {
                ua.Hangup();
                Console.WriteLine($"[{Ts}] 已主动挂断");
            }
            else
            {
                Console.WriteLine($"[{Ts}] 对方已挂断");
            }
            session.Close("call ended");
        }

        /// <summary>将 G.711 原始载荷解码后写入 WAV 文件</summary>
        private static void SaveWav(byte[] g711Bytes, int callId, int payloadType)
        {
            if (g711Bytes.Length == 0)
            {
                Console.WriteLine($"[{Ts}] 警告：通话 #{callId} 无音频数据，跳过保存");
                return;
            }

            // G.711 解码 → 16-bit PCM
            bool isALaw = payloadType == 8; // PCMA=8, PCMU=0
            var pcm = new short[g711Bytes.Length];
            for (int i = 0; i < g711Bytes.Length; i++)
                pcm[i] = isALaw ? ALawDecode(g711Bytes[i]) : MuLawDecode(g711Bytes[i]);

            string codec = isALaw ? "PCMA" : "PCMU";
            string tsStr = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"call_{tsStr}_{callId}_{codec}.wav";
            string path = Path.Combine(AppContext.BaseDirectory, fileName);

            WriteWavFile(path, pcm, SAMPLE_RATE_HZ, channels: 1);

            double seconds = g711Bytes.Length / (double)SAMPLE_RATE_HZ;
            Console.WriteLine($"[{Ts}] 录音已保存: {fileName}（时长 {seconds:F1} 秒）");
        }

        /// <summary>ITU-T G.711 μ-law 解码（PCMU，payload type 0）</summary>
        private static short MuLawDecode(byte mulaw)
        {
            const int BIAS = 0x84;
            mulaw = (byte)~mulaw;
            int t = ((mulaw & 0x0F) << 3) + BIAS;
            t <<= (mulaw & 0x70) >> 4;
            return (short)((mulaw & 0x80) != 0 ? BIAS - t : t - BIAS);
        }

        /// <summary>ITU-T G.711 A-law 解码（PCMA，payload type 8）</summary>
        private static short ALawDecode(byte alaw)
        {
            alaw ^= 0x55;
            int seg = (alaw & 0x70) >> 4;
            int t = (alaw & 0x0F) << 4;
            switch (seg)
            {
                case 0: t += 8; break;
                case 1: t += 0x108; break;
                default:
                    t <<= seg - 1;
                    t += 0x108 << (seg - 1);
                    break;
            }
            return (short)((alaw & 0x80) != 0 ? t : -t);
        }

        /// <summary>将 16-bit PCM 写入标准 WAV 文件（格式码 1，单声道，8000Hz）</summary>
        private static void WriteWavFile(string path, short[] pcm, int rate, int channels)
        {
            int dataSize = pcm.Length * 2;
            int byteRate = rate * channels * 2;
            using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(f);
            // RIFF header
            w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            w.Write(36 + dataSize);
            w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
            // fmt chunk
            w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            w.Write(16);                    // Subchunk1Size（PCM 固定 16）
            w.Write((short)1);              // AudioFormat: PCM
            w.Write((short)channels);       // NumChannels
            w.Write(rate);                  // SampleRate
            w.Write(byteRate);              // ByteRate
            w.Write((short)(channels * 2)); // BlockAlign
            w.Write((short)16);             // BitsPerSample
            // data chunk
            w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            w.Write(dataSize);
            foreach (var s in pcm) w.Write(s);
        }

        private static string Ts => DateTime.Now.ToString("HH:mm:ss");

        private static string GetLocalIP()
        {
            try
            {
                using var udp = new UdpClient();
                udp.Connect("8.8.8.8", 80);
                return ((IPEndPoint)udp.Client.LocalEndPoint!).Address.ToString();
            }
            catch { return "127.0.0.1"; }
        }
    }
}

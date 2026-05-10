using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace VeraMedia.Api.Services;

public sealed class EdgeTtsClient
{
    private const string TrustedToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string WssEndpoint = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";
    private const string ChromiumVersion = "143.0.3650.75";
    private const string SecMsGecVersion = "1-" + ChromiumVersion;
    private const long WinEpochOffset = 11644473600L;

    private static double _clockSkewSeconds;

    public async Task SynthesizeToFileAsync(
        string text,
        string voice,
        string rate,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var audioData = await SynthesizeAsync(text, voice, rate, cancellationToken);
        await File.WriteAllBytesAsync(outputPath, audioData, cancellationToken);
    }

    public async Task<byte[]> SynthesizeAsync(
        string text,
        string voice,
        string rate,
        CancellationToken cancellationToken)
    {
        Exception? lastEx = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await SynthesizeOnceAsync(text, voice, rate, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastEx = ex;
                if (attempt < 2)
                    await Task.Delay(500 * (attempt + 1), cancellationToken);
            }
        }
        throw lastEx!;
    }

    private async Task<byte[]> SynthesizeOnceAsync(
        string text, string voice, string rate, CancellationToken cancellationToken)
    {
        using var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;

        var connectionId = Guid.NewGuid().ToString("N");
        var secMsGec = GenerateSecMsGec();
        var muid = GenerateMuid();
        var url = $"{WssEndpoint}?TrustedClientToken={TrustedToken}&ConnectionId={connectionId}&Sec-MS-GEC={secMsGec}&Sec-MS-GEC-Version={SecMsGecVersion}";

        ws.Options.SetRequestHeader("User-Agent",
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromiumVersion} Safari/537.36 Edg/{ChromiumVersion}");
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Cookie", $"muid={muid};");

        try
        {
            await ws.ConnectAsync(new Uri(url), cancellationToken);
        }
        catch (WebSocketException)
        {
            TryAdjustClockSkew(ws);
            throw;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var configMsg = $"X-RequestId:{requestId}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n"
                        + """{"context":{"synthesis":{"audio":{"metadataoptions":{"sentenceBoundaryEnabled":"false","wordBoundaryEnabled":"true"},"outputFormat":"audio-24khz-48kbitrate-mono-mp3"}}}}""";
        await SendTextAsync(ws, configMsg, cancellationToken);

        var ssmlRequestId = Guid.NewGuid().ToString("N");
        var locale = voice.Contains('-') ? voice[..voice.LastIndexOf('-')] : "zh-CN";
        var escapedText = System.Security.SecurityElement.Escape(text);
        var ssml = $"""<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{locale}'><voice name='{voice}'><prosody rate='{rate}' pitch='+0Hz' volume='+0%'>{escapedText}</prosody></voice></speak>""";
        var ssmlMsg = $"X-RequestId:{ssmlRequestId}\r\nContent-Type:application/ssml+xml\r\nPath:ssml\r\n\r\n{ssml}";
        await SendTextAsync(ws, ssmlMsg, cancellationToken);

        var audioChunks = new List<byte[]>();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(60));

            while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
            {
                var (messageType, messageBytes) = await ReceiveFullMessageAsync(ws, cts.Token);

                if (messageType == WebSocketMessageType.Close) break;

                if (messageType == WebSocketMessageType.Text)
                {
                    var textMsg = Encoding.UTF8.GetString(messageBytes);
                    if (textMsg.Contains("Path:turn.end", StringComparison.OrdinalIgnoreCase))
                        break;
                    continue;
                }

                if (messageBytes.Length < 3) continue;

                var headerLength = BinaryPrimitives.ReadUInt16BigEndian(messageBytes);
                if (headerLength == 0 || messageBytes.Length <= headerLength + 2) continue;

                var headerText = Encoding.UTF8.GetString(messageBytes, 2, headerLength);
                if (!headerText.Contains("Path:audio", StringComparison.OrdinalIgnoreCase)) continue;

                var audioStart = 2 + headerLength;
                var audioLength = messageBytes.Length - audioStart;
                if (audioLength > 0)
                {
                    audioChunks.Add(messageBytes[audioStart..]);
                }
            }
        }
        finally
        {
            if (ws.State == WebSocketState.Open)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None); }
                catch { /* best effort */ }
            }
        }

        if (audioChunks.Count == 0)
            throw new InvalidOperationException($"Edge TTS returned no audio for voice '{voice}'.");

        var total = audioChunks.Sum(c => c.Length);
        var audio = new byte[total];
        var offset = 0;
        foreach (var chunk in audioChunks)
        {
            Array.Copy(chunk, 0, audio, offset, chunk.Length);
            offset += chunk.Length;
        }

        return audio;
    }

    private static void TryAdjustClockSkew(ClientWebSocket ws)
    {
        try
        {
            if (ws.HttpStatusCode != HttpStatusCode.Forbidden) return;
            if (ws.HttpResponseHeaders is not { } headers) return;
            if (!headers.TryGetValue("Date", out var dateValues)) return;
            var dateStr = dateValues.FirstOrDefault();
            if (string.IsNullOrEmpty(dateStr)) return;

            if (DateTimeOffset.TryParseExact(dateStr, "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var serverDate))
            {
                _clockSkewSeconds += serverDate.ToUnixTimeSeconds() - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
        }
        catch { /* best effort */ }
    }

    private static string GenerateSecMsGec()
    {
        var ticks = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + _clockSkewSeconds;
        ticks += WinEpochOffset;
        ticks -= ticks % 300;
        var windowsTicks = (long)(ticks * 1e7);
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes($"{windowsTicks}{TrustedToken}")));
    }

    private static string GenerateMuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        Random.Shared.NextBytes(bytes);
        return Convert.ToHexString(bytes);
    }

    private static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveFullMessageAsync(
        ClientWebSocket ws, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.Count > 0)
                    ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            return (result.MessageType, ms.ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task SendTextAsync(ClientWebSocket ws, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }
}

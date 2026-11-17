using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AiVoiceBridge.Core;

internal sealed class SoundCheck : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _token = Guid.NewGuid().ToString("N");
    private readonly string _host;
    private readonly string _page;
    private readonly string? _inputName;
    private readonly string _gameMicName;
    public string Url => $"http://{_host}/{_token}/";

    public SoundCheck(string? inputName, string gameMicName)
    {
        _inputName = inputName;
        _gameMicName = gameMicName;
        using var resource = typeof(SoundCheck).Assembly.GetManifestResourceStream("SoundCheck.html")!;
        using var reader = new StreamReader(resource);
        _page = reader.ReadToEnd();

        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        _host = $"127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}";
        reservation.Stop();
        _listener.Prefixes.Add($"http://{_host}/");
        try { _listener.Start(); }
        catch { _listener.Close(); throw; }
        _ = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        using var toAi = new CaptureLevel(_inputName);
        using var toGame = new CaptureLevel(_gameMicName);
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            var pending = _listener.GetContextAsync();
            try { context = await pending.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (TimeoutException)
            {
                toAi.Pause();
                toGame.Pause();
                // Resume on the next poll, including when a background tab wakes up.
                try { context = await pending; }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }

            var response = context.Response;
            try
            {
                string path = context.Request.RawUrl ?? "";
                if (context.Request.HttpMethod != "GET" || context.Request.UserHostName != _host ||
                    (path != $"/{_token}/" && path != $"/{_token}/levels"))
                {
                    response.StatusCode = 404;
                    continue;
                }
                response.Headers["Cache-Control"] = "no-store";
                response.Headers["Referrer-Policy"] = "no-referrer";
                response.Headers["X-Content-Type-Options"] = "nosniff";
                response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'";
                bool isPage = path.EndsWith('/');
                response.ContentType = isPage ? "text/html; charset=utf-8" : "application/json; charset=utf-8";
                string body = isPage ? _page : JsonSerializer.Serialize(new { input = toAi.Read(), game = toGame.Read() });
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes);
            }
            catch (Exception e) when (e is IOException or HttpListenerException or ObjectDisposedException) { }
            finally { response.Close(); }
        }
    }

    public void Dispose() => _listener.Close();

    private sealed class CaptureLevel(string? prefix) : IDisposable
    {
        private MMDevice? _device;
        private WasapiCapture? _capture;
        private long _lastAttempt = -5000;
        private long _lastSignal;
        private float _peak;
        private string? _error;

        private void Start(long now)
        {
            _lastAttempt = now;
            try
            {
                if (prefix == null) throw new InvalidOperationException("Для этого выхода не задан парный виртуальный микрофон.");
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    if (_device == null && device.FriendlyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        _device = device;
                    else device.Dispose();
                }
                if (_device == null) throw new InvalidOperationException($"Устройство «{prefix.TrimEnd(' ', '(')}» недоступно.");
                _capture = new WasapiCapture(_device, true, 30);
                var format = _capture.WaveFormat;
                PcmDecoder.Validate(format);
                var scratch = new float[4096];
                _capture.DataAvailable += (_, args) =>
                {
                    float peak = 0;
                    int sampleBytes = format.BitsPerSample / 8;
                    for (int offset = 0; offset < args.BytesRecorded;)
                    {
                        int length = Math.Min(args.BytesRecorded - offset, scratch.Length * sampleBytes);
                        int count = PcmDecoder.Decode(args.Buffer.AsSpan(offset, length), scratch, format);
                        for (int i = 0; i < count; i++)
                            if (float.IsFinite(scratch[i])) peak = Math.Max(peak, Math.Abs(scratch[i]));
                        offset += length;
                    }
                    // Hold short bursts long enough for a browser poll to detect them.
                    if (peak >= .001f)
                    {
                        Volatile.Write(ref _peak, peak);
                        Volatile.Write(ref _lastSignal, Environment.TickCount64);
                    }
                };
                _capture.RecordingStopped += (_, args) =>
                    Volatile.Write(ref _error, args.Exception?.Message ?? "Поток аудио остановлен.");
                Volatile.Write(ref _error, null);
                _capture.StartRecording();
            }
            catch (Exception e) { Pause(); Volatile.Write(ref _error, e.Message); }
        }

        public object Read()
        {
            long now = Environment.TickCount64;
            if ((_capture == null || Volatile.Read(ref _error) != null) && now - _lastAttempt >= 2000)
            {
                Pause();
                Start(now);
            }
            var error = Volatile.Read(ref _error);
            float level = now - Volatile.Read(ref _lastSignal) <= 450 ? Volatile.Read(ref _peak) : 0;
            return new { device = _device?.FriendlyName, level = Math.Clamp(level, 0, 1), error };
        }

        public void Pause()
        {
            _capture?.Dispose();
            _capture = null;
            _device?.Dispose();
            _device = null;
            _lastSignal = 0;
        }

        public void Dispose() => Pause();
    }
}


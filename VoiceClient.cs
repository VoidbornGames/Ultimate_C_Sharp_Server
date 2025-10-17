using NAudio.Wave;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class VoiceClient : IDisposable
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint _serverEndPoint;
    private readonly WaveInEvent _waveIn;
    private readonly WaveOutEvent _waveOut;
    private readonly BufferedWaveProvider _waveProvider;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private bool _disposed = false;
    private Timer _heartbeatTimer;

    public VoiceClient(string serverIp, int port)
    {
        Console.WriteLine($"[CLIENT] Initializing... Server: {serverIp}:{port}");

        _udp = new UdpClient(0);
        _serverEndPoint = new IPEndPoint(IPAddress.Parse(serverIp), port);

        // Clear voice config
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1), // 16 kHz, 16-bit, mono
            BufferMilliseconds = 20                   // small latency, safe UDP size
        };
        _waveIn.DataAvailable += WaveIn_DataAvailable;

        _waveProvider = new BufferedWaveProvider(_waveIn.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(100),
            DiscardOnBufferOverflow = true
        };

        _waveOut = new WaveOutEvent();
        _waveOut.Init(_waveProvider);

        Console.WriteLine("[CLIENT] Initialized successfully.");
    }

    private void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
    {
        if (_disposed || e.BytesRecorded == 0) return;

        try
        {
            // Split into smaller chunks to avoid UDP packet drop
            int chunkSize = 1024; // 1 KB per packet
            for (int offset = 0; offset < e.BytesRecorded; offset += chunkSize)
            {
                int size = Math.Min(chunkSize, e.BytesRecorded - offset);
                byte[] chunk = new byte[size];
                Array.Copy(e.Buffer, offset, chunk, 0, size);
                _udp.Send(chunk, size, _serverEndPoint);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIENT] ERROR sending audio: {ex.Message}");
        }
    }

    private void SendHeartbeat(object state)
    {
        if (_disposed) return;

        try
        {
            string message = "heartbeat";
            byte[] data = Encoding.UTF8.GetBytes(message);
            _udp.Send(data, data.Length, _serverEndPoint);
            Console.WriteLine("[CLIENT] Sent HEARTBEAT packet.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIENT] ERROR sending heartbeat: {ex.Message}");
        }
    }

    public async Task StartReceivingAsync()
    {
        Console.WriteLine("[CLIENT] Starting receive loop...");
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync();
                if (!_disposed && result.Buffer.Length > 0)
                {
                    _waveProvider.AddSamples(result.Buffer, 0, result.Buffer.Length);
                }
            }
            catch (ObjectDisposedException)
            {
                Console.WriteLine("[CLIENT] Receive loop stopped (UdpClient disposed).");
                break;
            }
            catch (Exception ex)
            {
                if (!_cts.Token.IsCancellationRequested)
                {
                    Console.WriteLine($"[CLIENT] ERROR receiving audio: {ex.Message}");
                }
            }
        }
    }

    public void Start()
    {
        Console.WriteLine("[CLIENT] Starting recording and playback...");
        try
        {
            _waveIn.StartRecording();
            _waveOut.Play();
            _ = StartReceivingAsync();

            // Start heartbeat timer
            // Turn off for production use!
            _heartbeatTimer = new Timer(SendHeartbeat, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));

            Console.WriteLine("[CLIENT] Started successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIENT] FATAL ERROR on Start(): {ex.Message}");
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        _waveIn.StopRecording();
        _waveOut.Stop();
        _heartbeatTimer?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        Stop();
        _waveIn?.Dispose();
        _waveOut?.Dispose();
        _udp?.Close();
        _udp?.Dispose();
        _cts?.Dispose();
    }
}

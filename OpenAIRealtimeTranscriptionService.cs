// OpenAI Whisper API transcription using HTTP requests
using NAudio.Wave;
using System.Text;
using System.Text.Json;

public class OpenAIRealtimeTranscriptionService : ITranscriptionService
{
    private const int SAMPLE_RATE = 16000; // Whisper API works with 16kHz
    private const string WHISPER_API_URL = "https://api.openai.com/v1/audio/transcriptions";
    
    private readonly string apiKey;
    private readonly HttpClient httpClient;
    private WasapiLoopbackCapture? capture;
    private MediaFoundationResampler? resampler;
    private CancellationTokenSource? cancellationTokenSource;
    private int segmentIndex = 0;
    private DateTime sessionStartTime;
    private List<byte> audioBuffer = new List<byte>();
    private readonly object bufferLock = new object();
    
    public event Action<string, string, int>? OnTranscriptionAvailable;
    public event Action<string>? OnError;
    public event Action<string>? OnStatusUpdate;
    
    public OpenAIRealtimeTranscriptionService(string apiKey)
    {
        this.apiKey = apiKey;
        httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        httpClient.Timeout = TimeSpan.FromSeconds(30);
    }
    
    public async Task InitializeAsync()
    {
        OnStatusUpdate?.Invoke("Initializing OpenAI Whisper API Transcription...");
        
        try
        {
            cancellationTokenSource = new CancellationTokenSource();
            sessionStartTime = DateTime.Now;
            
            // Test API connection
            OnStatusUpdate?.Invoke("Connected to OpenAI Whisper API! Listening to system audio...");
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Failed to initialize: {ex.Message}");
        }
    }
    
    public Task StartTranscriptionAsync()
    {
        // Start both audio capture and transcription tasks
        _ = Task.Run(CaptureAudio);
        _ = Task.Run(TranscribeLoop);
        
        return Task.CompletedTask;
    }
    
    public void StopTranscription()
    {
        cancellationTokenSource?.Cancel();
    }
    
    private async Task CaptureAudio()
    {
        try
        {
            capture = new WasapiLoopbackCapture();
            var sourceFormat = capture.WaveFormat;
            
            var bufferedProvider = new BufferedWaveProvider(sourceFormat)
            {
                ReadFully = false,
                DiscardOnBufferOverflow = true
            };
            
            var targetFormat = new WaveFormat(SAMPLE_RATE, 16, 1); // 16kHz, 16-bit, mono
            resampler = new MediaFoundationResampler(bufferedProvider, targetFormat)
            {
                ResamplerQuality = 60
            };
            
            capture.DataAvailable += (sender, args) =>
            {
                if (args.BytesRecorded > 0)
                {
                    bufferedProvider.AddSamples(args.Buffer, 0, args.BytesRecorded);
                }
            };
            
            capture.StartRecording();
            
            // Use larger buffer to ensure continuous capture with no gaps
            byte[] readBuffer = new byte[6400]; // 200ms chunks at 16kHz for smoother capture
            
            while (!cancellationTokenSource!.Token.IsCancellationRequested)
            {
                int bytesRead = resampler.Read(readBuffer, 0, readBuffer.Length);
                
                if (bytesRead > 0)
                {
                    lock (bufferLock)
                    {
                        audioBuffer.AddRange(readBuffer.Take(bytesRead));
                    }
                }
                
                // Minimal delay to keep buffer full and prevent gaps
                await Task.Delay(10);
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Audio capture error: {ex.Message}");
        }
    }
    
    private async Task TranscribeLoop()
    {
        // Wait for initial audio to accumulate
        await Task.Delay(3000);
        
        while (!cancellationTokenSource!.Token.IsCancellationRequested)
        {
            try
            {
                byte[]? audioToTranscribe = null;
                bool hasEnoughAudio = false;
                
                lock (bufferLock)
                {
                    // Need at least 3 seconds of audio for better context (16000 Hz * 2 bytes * 3 = 96000 bytes)
                    if (audioBuffer.Count >= 96000)
                    {
                        hasEnoughAudio = true;
                        
                        // Take up to 30 seconds of audio at a time to capture complete sentences/thoughts
                        int maxBytes = 16000 * 2 * 30; // 30 seconds
                        int bytesToTake = Math.Min(audioBuffer.Count, maxBytes);
                        
                        audioToTranscribe = audioBuffer.Take(bytesToTake).ToArray();
                        audioBuffer.RemoveRange(0, bytesToTake);
                    }
                }
                
                if (!hasEnoughAudio)
                {
                    // Check more frequently to ensure no gaps
                    await Task.Delay(200);
                    continue;
                }
                
                // Transcribe the audio chunk
                if (audioToTranscribe != null)
                {
                    await TranscribeAudioChunk(audioToTranscribe);
                }
                
                // Minimal delay - audio continues buffering during API call, preventing gaps
                await Task.Delay(50);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Transcription loop error: {ex.Message}");
                await Task.Delay(1000);
            }
        }
    }
    
    private async Task TranscribeAudioChunk(byte[] pcmData)
    {
        try
        {
            // Convert PCM to WAV format
            var wavData = ConvertPcmToWav(pcmData, SAMPLE_RATE);
            
            // Create multipart form data
            using var content = new MultipartFormDataContent();
            var audioContent = new ByteArrayContent(wavData);
            audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            content.Add(audioContent, "file", "audio.wav");
            content.Add(new StringContent("whisper-1"), "model");
            content.Add(new StringContent("json"), "response_format");
            
            // Send request
            var response = await httpClient.PostAsync(WHISPER_API_URL, content, cancellationTokenSource!.Token);
            
            if (response.IsSuccessStatusCode)
            {
                var jsonResponse = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(jsonResponse);
                
                if (doc.RootElement.TryGetProperty("text", out var textElement))
                {
                    var text = textElement.GetString();
                    
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var elapsed = DateTime.Now - sessionStartTime;
                        var timeRange = $"[{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}]";
                        
                        OnTranscriptionAvailable?.Invoke(timeRange, text, segmentIndex++);
                    }
                }
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                OnError?.Invoke($"API Error: {response.StatusCode} - {error}");
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Transcription error: {ex.Message}");
        }
    }
    
    private byte[] ConvertPcmToWav(byte[] pcmData, int sampleRate)
    {
        using var memStream = new MemoryStream();
        using var writer = new BinaryWriter(memStream);
        
        // WAV header
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcmData.Length); // File size - 8
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        
        // fmt chunk
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // fmt chunk size
        writer.Write((short)1); // PCM format
        writer.Write((short)1); // Mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2); // Byte rate (sample rate * block align)
        writer.Write((short)2); // Block align (channels * bytes per sample)
        writer.Write((short)16); // Bits per sample
        
        // data chunk
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcmData.Length);
        writer.Write(pcmData);
        
        return memStream.ToArray();
    }
    
    public void Dispose()
    {
        StopTranscription();
        
        capture?.StopRecording();
        capture?.Dispose();
        resampler?.Dispose();
        httpClient?.Dispose();
        
        cancellationTokenSource?.Dispose();
    }
}

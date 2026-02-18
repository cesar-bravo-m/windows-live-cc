using NAudio.Wave;
using System.Runtime.InteropServices;

/*
// P/Invoke wrapper for whisper.dll
static class WhisperNative
{
    private const string DllName = "whisper.dll";
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr whisper_context_default_params_by_ref();
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr whisper_init_from_file_with_params(string path_model, IntPtr cparams);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void whisper_free(IntPtr ctx);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr whisper_full_default_params_by_ref(int strategy);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int whisper_full(IntPtr ctx, IntPtr wparams, float[] samples, int n_samples);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int whisper_full_n_segments(IntPtr ctx);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr whisper_full_get_segment_text(IntPtr ctx, int i_segment);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern long whisper_full_get_segment_t0(IntPtr ctx, int i_segment);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern long whisper_full_get_segment_t1(IntPtr ctx, int i_segment);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void whisper_free_params(IntPtr wparams);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void whisper_free_context_params(IntPtr cparams);

    // Whisper sampling strategies
    public const int WHISPER_SAMPLING_GREEDY = 0;
    public const int WHISPER_SAMPLING_BEAM_SEARCH = 10;
}

public class WhisperTranscriptionService : ITranscriptionService
{
    private const int WHISPER_SAMPLE_RATE = 16000;
    private const int CHUNK_LENGTH_SECONDS = 1;
    
    private IntPtr whisperCtx = IntPtr.Zero;
    private WasapiLoopbackCapture? capture;
    private MediaFoundationResampler? resampler;
    private int segmentIndex = 0;
    private bool isRunning = false;
    
    public event Action<string, string, int>? OnTranscriptionAvailable;
    public event Action<string>? OnError;
    public event Action<string>? OnStatusUpdate;
    
    public async Task InitializeAsync()
    {
        string modelPath = @".\ggml-large-v3.bin";
        
        if (!File.Exists(modelPath))
        {
            OnError?.Invoke($"Model file not found at {Directory.GetCurrentDirectory()}");
            return;
        }
        
        OnStatusUpdate?.Invoke("Loading Whisper model...");
        
        await Task.Run(() =>
        {
            var cparams = WhisperNative.whisper_context_default_params_by_ref();
            whisperCtx = WhisperNative.whisper_init_from_file_with_params(modelPath, cparams);
            WhisperNative.whisper_free_context_params(cparams);
        });
        
        if (whisperCtx == IntPtr.Zero)
        {
            OnError?.Invoke("Failed to initialize whisper context");
            return;
        }
        
        OnStatusUpdate?.Invoke("Model loaded! Listening to system audio...");
    }
    
    public Task StartTranscriptionAsync()
    {
        if (whisperCtx == IntPtr.Zero)
        {
            OnError?.Invoke("Whisper context not initialized");
            return Task.CompletedTask;
        }
        
        return Task.Run(() => RunTranscription());
    }
    
    public void StopTranscription()
    {
        isRunning = false;
    }
    
    private void RunTranscription()
    {
        capture = new WasapiLoopbackCapture();
        var sourceFormat = capture.WaveFormat;
        
        var bufferedProvider = new BufferedWaveProvider(sourceFormat)
        {
            ReadFully = false,
            DiscardOnBufferOverflow = true
        };
        
        var targetFormat = new WaveFormat(WHISPER_SAMPLE_RATE, 16, 1);
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
        isRunning = true;
        
        var audioBuffer = new List<float>();
        int samplesPerChunk = WHISPER_SAMPLE_RATE * CHUNK_LENGTH_SECONDS;
        
        byte[] readBuffer = new byte[targetFormat.AverageBytesPerSecond / 10];
        
        while (isRunning)
        {
            int bytesRead = resampler.Read(readBuffer, 0, readBuffer.Length);
            
            if (bytesRead > 0)
            {
                int sampleCount = bytesRead / 2;
                
                for (int i = 0; i < sampleCount; i++)
                {
                    short sample = BitConverter.ToInt16(readBuffer, i * 2);
                    float floatSample = sample / 32768f;
                    audioBuffer.Add(floatSample);
                }
                
                if (audioBuffer.Count >= samplesPerChunk)
                {
                    ProcessAudioChunk(audioBuffer.ToArray());
                    
                    int overlapSamples = WHISPER_SAMPLE_RATE * 1;
                    if (audioBuffer.Count > overlapSamples)
                    {
                        audioBuffer.RemoveRange(0, audioBuffer.Count - overlapSamples);
                    }
                    else
                    {
                        audioBuffer.Clear();
                    }
                }
            }
            
            Thread.Sleep(50);
        }
    }
    
    private void ProcessAudioChunk(float[] samples)
    {
        var wparams = WhisperNative.whisper_full_default_params_by_ref(WhisperNative.WHISPER_SAMPLING_GREEDY);
        
        try
        {
            int result = WhisperNative.whisper_full(whisperCtx, wparams, samples, samples.Length);
            
            if (result != 0)
            {
                return;
            }
            
            int nSegments = WhisperNative.whisper_full_n_segments(whisperCtx);
            
            for (int i = 0; i < nSegments; i++)
            {
                IntPtr textPtr = WhisperNative.whisper_full_get_segment_text(whisperCtx, i);
                string text = Marshal.PtrToStringAnsi(textPtr) ?? "";
                
                text = text.Trim();
                
                if (!string.IsNullOrWhiteSpace(text))
                {
                    long t0 = WhisperNative.whisper_full_get_segment_t0(whisperCtx, i);
                    long t1 = WhisperNative.whisper_full_get_segment_t1(whisperCtx, i);
                    
                    int t0_sec = (int)(t0 / 100);
                    int t1_sec = (int)(t1 / 100);
                    string time0 = $"{t0_sec / 60:D2}:{t0_sec % 60:D2}";
                    string time1 = $"{t1_sec / 60:D2}:{t1_sec % 60:D2}";
                    
                    string timeRange = $"[{time0} → {time1}]";
                    OnTranscriptionAvailable?.Invoke(timeRange, text, segmentIndex++);
                }
            }
        }
        finally
        {
            WhisperNative.whisper_free_params(wparams);
        }
    }
    
    public void Dispose()
    {
        StopTranscription();
        
        capture?.StopRecording();
        capture?.Dispose();
        resampler?.Dispose();
        
        if (whisperCtx != IntPtr.Zero)
        {
            WhisperNative.whisper_free(whisperCtx);
            whisperCtx = IntPtr.Zero;
        }
    }
}
*/

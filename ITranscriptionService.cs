// Interface for real-time transcription services
public interface ITranscriptionService : IDisposable
{
    /// <summary>
    /// Event raised when a transcription segment is available
    /// </summary>
    event Action<string, string, int>? OnTranscriptionAvailable; // timeRange, text, index
    
    /// <summary>
    /// Event raised when an error occurs
    /// </summary>
    event Action<string>? OnError;
    
    /// <summary>
    /// Event raised when initialization is complete
    /// </summary>
    event Action<string>? OnStatusUpdate;
    
    /// <summary>
    /// Initialize the transcription service
    /// </summary>
    Task InitializeAsync();
    
    /// <summary>
    /// Start real-time transcription
    /// </summary>
    Task StartTranscriptionAsync();
    
    /// <summary>
    /// Stop transcription
    /// </summary>
    void StopTranscription();
}

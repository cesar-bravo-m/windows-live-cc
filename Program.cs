// Real-time speech recognition using OpenAI Real-time API from WASAPI loopback (system audio)
// with WinUI floating caption window

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Windows.Graphics;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;

// Caption line data
public class CaptionLine
{
    public string TimeRange { get; set; } = "";
    public string Text { get; set; } = "";
    public int Index { get; set; }
}

// WinUI Application
public class App : Application
{
    private Window? m_window;
    
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        m_window = new CaptionWindow();
        m_window.Activate();
    }
}

// Floating Caption Window
public class CaptionWindow : Window
{
    private ScrollViewer scrollViewer = null!;
    private StackPanel captionContainer = null!;
    private Button syncButton = null!;
    private Button micButton = null!;
    private ToggleSwitch translateSwitch = null!;
    private ObservableCollection<CaptionLine> captions = new ObservableCollection<CaptionLine>();
    private bool autoScroll = true;
    private bool isTranscribing = false;
    private DispatcherQueue dispatcherQueue = null!;
    private ITranscriptionService? transcriptionService;
    private HttpClient httpClient = new HttpClient();
    
    public CaptionWindow()
    {
        dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        
        Title = "Windows Live CC";
        
        // Set window size and position - compact like Spotify
        var appWindow = this.AppWindow;
        appWindow.Resize(new SizeInt32(500, 400));
        
        // Keep window always on top
        appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.CompactOverlay);
        
        BuildUI();
        
        // Cleanup on window close
        Closed += (sender, args) =>
        {
            transcriptionService?.StopTranscription();
            transcriptionService?.Dispose();
            httpClient?.Dispose();
        };
    }
    
    private void BuildUI()
    {
        var grid = new Grid();
        grid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(245, 18, 18, 18));
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        
        scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(30, 20, 30, 20)
        };
        
        captionContainer = new StackPanel
        {
            Spacing = 20,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 440
        };
        
        scrollViewer.Content = captionContainer;
        scrollViewer.ViewChanged += ScrollViewer_ViewChanged;
        
        Grid.SetRow(scrollViewer, 0);
        grid.Children.Add(scrollViewer);
        
        // Sync button
        syncButton = new Button
        {
            Content = "⇓ Ir al actual",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 15),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 40, 40, 40)),
            Foreground = new SolidColorBrush(Colors.White),
            Padding = new Thickness(20, 10, 20, 10),
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(20),
            BorderThickness = new Thickness(0),
            Visibility = Visibility.Collapsed
        };
        syncButton.Click += SyncButton_Click;
        
        Grid.SetRow(syncButton, 1);
        grid.Children.Add(syncButton);
        
        // Footer with translation switch and microphone button - Spotify-like
        var footer = new StackPanel
        {
            Padding = new Thickness(15, 10, 15, 15),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 24, 24, 24)),
            Spacing = 10
        };
        
        // Translation switch
        translateSwitch = new ToggleSwitch
        {
            Header = "Traducir a español",
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Colors.White),
            IsOn = false
        };
        footer.Children.Add(translateSwitch);
        
        // Microphone button
        micButton = new Button
        {
            Content = "Iniciar transcripción",
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 30, 215, 96)), // Spotify green
            Foreground = new SolidColorBrush(Colors.White),
            Padding = new Thickness(30, 12, 30, 12),
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(20),
            BorderThickness = new Thickness(0)
        };
        micButton.Click += MicButton_Click;
        
        footer.Children.Add(micButton);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        
        Content = grid;
    }
    
    private void ScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight - 10)
        {
            // User scrolled up, disable auto-scroll and show sync button
            autoScroll = false;
            syncButton.Visibility = Visibility.Visible;
        }
        else
        {
            // User is at bottom, enable auto-scroll and hide sync button
            autoScroll = true;
            syncButton.Visibility = Visibility.Collapsed;
        }
    }
    
    private void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        autoScroll = true;
        ScrollToBottom();
        syncButton.Visibility = Visibility.Collapsed;
    }
    
    private void MicButton_Click(object sender, RoutedEventArgs e)
    {
        if (!isTranscribing)
        {
            // Start transcription
            isTranscribing = true;
            micButton.Content = "Detener transcripción";
            micButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 70, 50)); // Red for stop
            Task.Run(InitializeAndRun);
        }
        else
        {
            // Stop transcription
            isTranscribing = false;
            micButton.Content = "Iniciar transcripción";
            micButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 30, 215, 96)); // Green for start
            transcriptionService?.StopTranscription();
        }
    }
    
    private void ScrollToBottom()
    {
        scrollViewer.ChangeView(null, scrollViewer.ScrollableHeight, null);
    }
    
    private async Task<string> TranslateToSpanish(string text)
    {
        try
        {
            var requestBody = new
            {
                model = "gpt-3.5-turbo",
                messages = new[]
                {
                    new { role = "system", content = "You are a translator. Translate the following text to Spanish. Only respond with the translation, nothing else." },
                    new { role = "user", content = text }
                },
                temperature = 0.3
            };
            
            var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            
            httpClient.DefaultRequestHeaders.Clear();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {Program.OPENAPI_KEY}");
            
            var response = await httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);
            var responseText = await response.Content.ReadAsStringAsync();
            
            if (response.IsSuccessStatusCode)
            {
                using var jsonDoc = System.Text.Json.JsonDocument.Parse(responseText);
                var translatedText = jsonDoc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();
                
                return translatedText ?? text;
            }
            else
            {
                return text; // Return original text if translation fails
            }
        }
        catch
        {
            return text; // Return original text on error
        }
    }
    
    private void AddCaption(string timeRange, string text, int index)
    {
        dispatcherQueue.TryEnqueue(() =>
        {
            var lineContainer = new StackPanel
            {
                Margin = new Thickness(0, 10, 0, 10),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            
            var captionText = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 26,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                LineHeight = 36,
                Opacity = 0.95
            };
            lineContainer.Children.Add(captionText);
            
            // Add to container
            captionContainer.Children.Add(lineContainer);
            
            if (autoScroll)
            {
                // Delay scroll to allow layout update
                Task.Delay(100).ContinueWith(_ =>
                {
                    dispatcherQueue.TryEnqueue(() => ScrollToBottom());
                });
            }
        });
    }
    
    private async Task InitializeAndRun()
    {
        if (!isTranscribing) return; // Only run if transcription is active
        
        try
        {
            // Use OpenAI Real-time Transcription Service
            if (transcriptionService == null)
            {
                transcriptionService = new OpenAIRealtimeTranscriptionService(Program.OPENAPI_KEY);
                
                // Wire up event handlers
                transcriptionService.OnTranscriptionAvailable += (timeRange, text, index) =>
                {
                    // Handle translation on background thread
                    Task.Run(async () =>
                    {
                        string displayText = text;
                        
                        // Check if translation is enabled (read from UI thread)
                        bool shouldTranslate = false;
                        bool checkCompleted = false;
                        
                        dispatcherQueue.TryEnqueue(() =>
                        {
                            shouldTranslate = translateSwitch.IsOn;
                            checkCompleted = true;
                        });
                        
                        // Wait for UI thread to complete the check
                        while (!checkCompleted)
                        {
                            await Task.Delay(1);
                        }
                        
                        // Translate if enabled
                        if (shouldTranslate)
                        {
                            displayText = await TranslateToSpanish(text);
                        }
                        
                        // Add caption on UI thread
                        AddCaption(timeRange, displayText, index);
                    });
                };
                
                transcriptionService.OnError += (errorMessage) =>
                {
                    AddCaption("ERROR", errorMessage, 0);
                };
                
                transcriptionService.OnStatusUpdate += (statusMessage) =>
                {
                    AddCaption("INFO", statusMessage, 0);
                };
            }
            
            // Initialize and start
            await transcriptionService.InitializeAsync();
            await transcriptionService.StartTranscriptionAsync();
        }
        catch (Exception ex)
        {
            AddCaption("ERROR", $"Failed to start transcription: {ex.Message}", 0);
            
            // Reset button state on error
            dispatcherQueue.TryEnqueue(() =>
            {
                isTranscribing = false;
                micButton.Content = "Iniciar transcripción";
                micButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 30, 215, 96));
            });
        }
    }
}

// Program entry point
class Program
{
    // Colocar aquí la llave de OpenAI
    public static string OPENAPI_KEY = "";
    
    [STAThread]
    static void Main(string[] args)
    {
        // For self-contained deployment, no bootstrap needed
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start((p) =>
        {
            _ = new App();
        });
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using ProtoDeck.Models;
using ProtoDeck.Services;

namespace ProtoDeck.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxResponseChars = 8_000_000;

    private readonly CollectionStore _store = new();
    private readonly List<RequestCollection> _collections;

    // Response buffer: written from gRPC threads, flushed to the UI at most once per dispatcher turn.
    private readonly StringBuilder _buffer = new();
    private readonly object _gate = new();
    private int _flushScheduled;
    private int _messageCount;
    private bool _truncated;

    private GrpcChannel? _channel;
    private (string Address, bool Insecure) _channelKey;
    private SavedRequest? _pending;
    private bool _suppressTemplate;

    private CallSession? _session;
    private Stopwatch _sessionWatch = new();
    private int _sentCount;

    /// <summary>Set by the view; returns local paths of the picked .proto files.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickProtoFiles { get; set; }

    public ObservableCollection<TreeNode> Services { get; } = [];
    public ObservableCollection<TreeNode> CollectionNodes { get; } = [];

    public bool IsInteractive => SelectedMethod?.IsClientStreaming == true;
    public bool IsOneShot => !IsInteractive;
    public bool IsStreamingMethod => SelectedMethod is { } m && (m.IsClientStreaming || m.IsServerStreaming);
    public bool NoSchema => !HasSchema;
    public bool NoCollections => CollectionNodes.Count == 0;
    public bool CanDeleteSaved => SelectedCollectionNode is { Request: not null, Collection: not null };

    // connection / schema
    [ObservableProperty] private string _address = "http://localhost:5000";
    [ObservableProperty] private bool _insecure;
    [ObservableProperty] private decimal? _timeoutSeconds = 30;
    [ObservableProperty] private string _importPaths = "";
    [ObservableProperty] private string _schemaSource = "No schema loaded";
    [ObservableProperty] private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoSchema))]
    private bool _hasSchema;

    // method
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenStreamCommand))]
    private MethodDescriptor? _selectedMethod;

    [ObservableProperty] private bool _hasMethod;
    [ObservableProperty] private string _methodName = "";
    [ObservableProperty] private string _methodKind = "";

    [ObservableProperty] private TreeNode? _selectedServiceNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteSaved))]
    private TreeNode? _selectedCollectionNode;

    // editors
    [ObservableProperty] private string _body = "{}";
    [ObservableProperty] private string _metadataText = "";
    [ObservableProperty] private string _collectionName = "Default";
    [ObservableProperty] private string _requestName = "";

    // response
    [ObservableProperty] private string _responseText = "";
    [ObservableProperty] private string _responseShapeText = "";
    [ObservableProperty] private string _headersText = "";
    [ObservableProperty] private string _trailersText = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _resultCode = "";
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private bool _resultIsOk;
    [ObservableProperty] private bool _resultIsError;

    // status bar
    [ObservableProperty] private string _statusLine = "Ready";
    [ObservableProperty] private bool _statusIsOk;
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _statusIsBusy;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenStreamCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    [NotifyCanExecuteChangedFor(nameof(EndStreamCommand))]
    [NotifyCanExecuteChangedFor(nameof(AbortStreamCommand))]
    private bool _isSessionOpen;

    public MainViewModel()
    {
        _collections = _store.Load();
        RebuildCollectionTree();
    }

    // ───────────────────────── status ─────────────────────────

    // Status texts are authored in this class; the dot colour is derived from their prefix.
    // Outcome lines (ShowOutcome) override the flags explicitly afterwards.
    partial void OnStatusLineChanged(string value)
    {
        static bool Starts(string v, params string[] prefixes) =>
            prefixes.Any(p => v.StartsWith(p, StringComparison.Ordinal));

        var error = Starts(value, "Connect failed", "Error", "Proto load failed", "Open failed", "Send failed",
            "End stream failed", "Invalid JSON", "Could not");
        var busy = Starts(value, "Calling", "Connecting", "Parsing");
        StatusIsError = error;
        StatusIsBusy = busy;
        StatusIsOk = !error && !busy && Starts(value, "Loaded", "Saved", "Sent message", "Stream open");
    }

    // ───────────────────────── selection ─────────────────────────

    partial void OnSelectedServiceNodeChanged(TreeNode? value)
    {
        if (value?.Method is { } method) SelectedMethod = method;
    }

    partial void OnSelectedCollectionNodeChanged(TreeNode? value)
    {
        if (value?.Request is { } request) LoadRequest(request, value.Collection);
    }

    partial void OnSelectedMethodChanged(MethodDescriptor? value)
    {
        OnPropertyChanged(nameof(IsInteractive));
        OnPropertyChanged(nameof(IsOneShot));
        OnPropertyChanged(nameof(IsStreamingMethod));

        HasMethod = value is not null;
        if (value is null)
        {
            MethodName = "";
            MethodKind = "";
            ResponseShapeText = "";
            return;
        }

        MethodName = $"{value.Service.FullName}/{value.Name}";
        MethodKind = Kind(value).ToUpperInvariant();
        ResponseShapeText = ProtoJsonCodec.Template(value.OutputType).ToJsonString(ProtoJsonCodec.Pretty);
        if (!_suppressTemplate) ApplyTemplate(value);
    }

    private void ApplyTemplate(MethodDescriptor method) =>
        Body = ProtoJsonCodec.Template(method.InputType).ToJsonString(ProtoJsonCodec.Pretty);

    private static string Kind(MethodDescriptor m) => (m.IsClientStreaming, m.IsServerStreaming) switch
    {
        (false, false) => "unary",
        (false, true) => "server streaming",
        (true, false) => "client streaming",
        _ => "bidirectional streaming"
    };

    // ───────────────────────── schema sources ─────────────────────────

    [RelayCommand]
    private async Task LoadProtosAsync()
    {
        if (PickProtoFiles is null) return;

        var picked = await PickProtoFiles();
        if (picked.Count == 0) return;

        IsBusy = true;
        StatusLine = "Parsing .proto files…";
        try
        {
            var extra = ImportPaths.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var services = await Task.Run(() => ProtoFileLoader.Load(picked, extra));
            ShowServices(services, $"proto · {string.Join(", ", picked.Select(Path.GetFileName))}", reflected: false);
        }
        catch (Exception e)
        {
            StatusLine = $"Proto load failed: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        IsBusy = true;
        StatusLine = "Connecting…";
        try
        {
            var channel = EnsureChannel(force: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var services = await ReflectionClient.LoadAsync(channel, timeout.Token);
            ShowServices(services, $"reflection · {Address.Trim()}", reflected: true);
            IsConnected = true;
        }
        catch (Exception e)
        {
            IsConnected = false;
            _channel?.Dispose();
            _channel = null;
            StatusLine = $"Connect failed: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        AbortStream();
        _channel?.Dispose();
        _channel = null;
        IsConnected = false;
        IsBusy = false;
        Services.Clear();
        SelectedServiceNode = null;
        SelectedMethod = null;
        HasSchema = false;
        SchemaSource = "Disconnected";
        StatusLine = "Disconnected from server";
    }

    private void ShowServices(IReadOnlyList<ServiceDescriptor> services, string source, bool reflected)
    {
        if (!reflected)
        {
            _channel?.Dispose();
            _channel = null;
            IsConnected = false;
        }

        Services.Clear();
        foreach (var service in services)
        {
            var node = new TreeNode { Title = service.FullName };
            foreach (var method in service.Methods)
            {
                var streaming = method.IsClientStreaming || method.IsServerStreaming;
                node.Children.Add(new TreeNode
                {
                    Title = streaming ? $"{method.Name}  ·  {Kind(method)}" : method.Name,
                    Method = method
                });
            }
            Services.Add(node);
        }

        HasSchema = services.Count > 0;
        SchemaSource = source;
        StatusLine = $"Loaded {services.Count} service(s) · {source}";
        if (_pending is { } pending) LoadRequest(pending, null);
    }

    private GrpcChannel EnsureChannel(bool force = false)
    {
        var address = Address.Trim();
        if (!address.Contains("://", StringComparison.Ordinal)) address = "http://" + address;

        var key = (address, Insecure);
        if (!force && _channel is not null && _channelKey == key) return _channel;

        _channel?.Dispose();

        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30)
        };
        if (Insecure)
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;

        _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = 64 * 1024 * 1024,
            MaxSendMessageSize = 64 * 1024 * 1024
        });
        _channelKey = key;
        return _channel;
    }

    private TimeSpan? Deadline() =>
        TimeoutSeconds is > 0 ? TimeSpan.FromSeconds((double)TimeoutSeconds.Value) : null;

    // ───────────────────────── one-shot calls (unary / server streaming) ─────────────────────────

    private bool CanSend() => SelectedMethod is { IsClientStreaming: false };

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanSend))]
    private async Task SendAsync(CancellationToken ct)
    {
        var method = SelectedMethod!;
        ResetResponse();
        IsBusy = true;
        StatusLine = "Calling…";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var outcome = await DynamicInvoker.InvokeAsync(
                EnsureChannel(), method, Body, ParseMetadata(MetadataText), Deadline(), AppendResponse, ct);
            stopwatch.Stop();
            ShowOutcome(outcome, stopwatch.ElapsedMilliseconds, sent: null);
        }
        catch (OperationCanceledException)
        {
            ShowFailure("CANCELLED", $"{stopwatch.ElapsedMilliseconds} ms", $"Cancelled after {stopwatch.ElapsedMilliseconds} ms");
        }
        catch (Exception e)
        {
            ShowFailure("ERROR", $"{stopwatch.ElapsedMilliseconds} ms", $"Error: {e.Message}");
        }
        finally
        {
            IsBusy = false;
            FlushResponse();
        }
    }

    private void ShowOutcome(CallOutcome outcome, long elapsedMs, int? sent)
    {
        HeadersText = Format(outcome.Headers);
        TrailersText = Format(outcome.Trailers);

        var ok = outcome.Code == StatusCode.OK;
        var sentText = sent is { } n ? $" · {n} sent" : "";

        ResultCode = ok ? "OK" : Regex.Replace(outcome.Code.ToString(), "(?<!^)([A-Z])", "_$1").ToUpperInvariant();
        ResultSummary = $"{elapsedMs} ms{sentText} · {_messageCount} received";
        ResultIsOk = ok;
        ResultIsError = !ok;
        HasResult = true;

        var detail = string.IsNullOrEmpty(outcome.Detail) ? "" : $" — {outcome.Detail}";
        StatusLine = $"{ResultCode}{detail} · {ResultSummary}";
        StatusIsOk = ok;
        StatusIsError = !ok;
        StatusIsBusy = false;
    }

    private void ShowFailure(string code, string summary, string status)
    {
        ResultCode = code;
        ResultSummary = summary;
        ResultIsOk = false;
        ResultIsError = true;
        HasResult = true;
        StatusLine = status;
        StatusIsOk = false;
        StatusIsError = true;
        StatusIsBusy = false;
    }

    // ───────────────────────── interactive streaming (client / bidi) ─────────────────────────

    private bool CanOpenStream() => !IsSessionOpen && SelectedMethod is { IsClientStreaming: true };
    private bool CanUseSession() => IsSessionOpen;

    [RelayCommand(CanExecute = nameof(CanOpenStream))]
    private void OpenStream()
    {
        if (SelectedMethod is not { IsClientStreaming: true } method || _session is not null) return;

        try
        {
            ResetResponse();
            var session = CallSession.Start(EnsureChannel(), method, ParseMetadata(MetadataText), Deadline(),
                AppendResponse, CancellationToken.None);

            _session = session;
            _sentCount = 0;
            _sessionWatch = Stopwatch.StartNew();
            IsSessionOpen = true;
            IsBusy = true;
            StatusLine = "Stream open. Edit the body, then Send message. End stream half-closes.";
            _ = WatchSessionAsync(session);
        }
        catch (Exception e)
        {
            StatusLine = $"Open failed: {e.Message}";
        }
    }

    private async Task WatchSessionAsync(CallSession session)
    {
        var outcome = await session.Completion; // never throws
        if (!ReferenceEquals(_session, session)) return;

        _sessionWatch.Stop();
        _session = null;
        IsSessionOpen = false;
        IsBusy = false;
        FlushResponse();
        ShowOutcome(outcome, _sessionWatch.ElapsedMilliseconds, _sentCount);
        session.Dispose();
    }

    [RelayCommand(CanExecute = nameof(CanUseSession))]
    private async Task SendMessageAsync()
    {
        if (_session is not { } session) return;

        try
        {
            await session.SendAsync(Body);
            _sentCount++;
            StatusLine = $"Sent message #{_sentCount}";
        }
        catch (Exception e)
        {
            StatusLine = $"Send failed: {e.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSession))]
    private async Task EndStreamAsync()
    {
        if (_session is not { } session) return;
        try
        {
            await session.CompleteAsync();
            StatusLine = $"Request stream closed after {_sentCount} message(s). Waiting for the server…";
        }
        catch (Exception e)
        {
            StatusLine = $"End stream failed: {e.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSession))]
    private void AbortStream() => _session?.Cancel();

    // ───────────────────────── response buffer ─────────────────────────

    [RelayCommand]
    private void ClearResponse()
    {
        ResetResponse();
        StatusLine = "Response cleared";
    }

    private void ResetResponse()
    {
        lock (_gate)
        {
            _buffer.Clear();
            _messageCount = 0;
            _truncated = false;
        }
        ResponseText = "";
        HeadersText = "";
        TrailersText = "";
        HasResult = false;
        ResultCode = "";
        ResultSummary = "";
        ResultIsOk = false;
        ResultIsError = false;
    }

    private void AppendResponse(string json)
    {
        lock (_gate)
        {
            _messageCount++;
            if (_buffer.Length < MaxResponseChars)
            {
                _buffer.Append("// #").Append(_messageCount).Append("  ")
                       .Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append('\n')
                       .Append(json).Append("\n\n");
            }
            else if (!_truncated)
            {
                _truncated = true;
                _buffer.Append("// … output truncated; further messages are counted but not shown.\n");
            }
        }

        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
            Dispatcher.UIThread.Post(FlushResponse, DispatcherPriority.Background);
    }

    private void FlushResponse()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        string text;
        lock (_gate) text = _buffer.ToString();
        ResponseText = text;
    }

    private static Metadata ParseMetadata(string text)
    {
        var metadata = new Metadata();
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) throw new FormatException($"Metadata line {lineNumber}: expected 'key: value'.");

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (key.EndsWith("-bin", StringComparison.Ordinal)) metadata.Add(key, Convert.FromBase64String(value));
            else metadata.Add(key, value);
        }
        return metadata;
    }

    private static string Format(Metadata metadata) =>
        string.Join('\n', metadata.Select(e => $"{e.Key}: {(e.IsBinary ? Convert.ToBase64String(e.ValueBytes) : e.Value)}"));

    // ───────────────────────── editor helpers ─────────────────────────

    [RelayCommand]
    private void FormatBody()
    {
        try
        {
            var node = JsonNode.Parse(Body, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            Body = node?.ToJsonString(ProtoJsonCodec.Pretty) ?? "{}";
        }
        catch (JsonException e)
        {
            StatusLine = $"Invalid JSON: {e.Message}";
        }
    }

    [RelayCommand]
    private void ResetBody()
    {
        if (SelectedMethod is { } method) ApplyTemplate(method);
    }

    // ───────────────────────── collections ─────────────────────────

    [RelayCommand]
    private void SaveRequest()
    {
        if (SelectedMethod is not { } method)
        {
            StatusLine = "Could not save: select a method first.";
            return;
        }

        var collectionName = string.IsNullOrWhiteSpace(CollectionName) ? "Default" : CollectionName.Trim();
        var requestName = string.IsNullOrWhiteSpace(RequestName) ? method.Name : RequestName.Trim();

        var collection = _collections.FirstOrDefault(c => c.Name == collectionName);
        if (collection is null)
        {
            collection = new RequestCollection { Name = collectionName };
            _collections.Add(collection);
        }

        var saved = new SavedRequest
        {
            Name = requestName,
            Address = Address.Trim(),
            Insecure = Insecure,
            TimeoutSeconds = (int)(TimeoutSeconds ?? 0),
            Service = method.Service.FullName,
            Method = method.Name,
            Body = Body,
            Metadata = MetadataText
        };

        var index = collection.Requests.FindIndex(r => r.Name == requestName);
        if (index >= 0) collection.Requests[index] = saved;
        else collection.Requests.Add(saved);

        PersistCollections($"Saved “{requestName}” to {collectionName}");
    }

    [RelayCommand]
    private void DeleteSaved()
    {
        if (SelectedCollectionNode is not { Request: { } request, Collection: { } collection })
        {
            StatusLine = "Could not delete: select a saved request in Collections.";
            return;
        }

        collection.Requests.Remove(request);
        if (collection.Requests.Count == 0) _collections.Remove(collection);
        PersistCollections($"Saved collections updated: deleted “{request.Name}”");
    }

    private void PersistCollections(string message)
    {
        try
        {
            _store.Save(_collections);
            StatusLine = message;
        }
        catch (Exception e)
        {
            StatusLine = $"Could not write collections: {e.Message}";
        }
        RebuildCollectionTree();
    }

    private void RebuildCollectionTree()
    {
        CollectionNodes.Clear();
        foreach (var collection in _collections)
        {
            var node = new TreeNode { Title = collection.Name, Collection = collection };
            foreach (var request in collection.Requests)
            {
                node.Children.Add(new TreeNode
                {
                    Title = $"{request.Name}  ·  {request.Method}",
                    Request = request,
                    Collection = collection
                });
            }
            CollectionNodes.Add(node);
        }
        OnPropertyChanged(nameof(NoCollections));
    }

    private void LoadRequest(SavedRequest request, RequestCollection? collection)
    {
        Address = request.Address;
        Insecure = request.Insecure;
        TimeoutSeconds = request.TimeoutSeconds;
        MetadataText = request.Metadata;
        RequestName = request.Name;
        if (collection is not null) CollectionName = collection.Name;

        var method = FindMethod(request.Service, request.Method);
        if (method is null)
        {
            _pending = request;
            Body = request.Body;
            StatusLine = $"{request.Service}/{request.Method} is not loaded. Load the .proto or press Connect.";
            return;
        }

        _pending = null;
        _suppressTemplate = true;
        try { SelectedMethod = method; }
        finally { _suppressTemplate = false; }
        Body = request.Body;
    }

    private MethodDescriptor? FindMethod(string service, string method) =>
        Services.Where(s => s.Title == service)
                .SelectMany(s => s.Children)
                .Select(c => c.Method)
                .FirstOrDefault(m => m is not null && m.Name == method);
}

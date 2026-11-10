using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LucidStream.Core.Cloud;
using LucidStream.Core.Common;
using LucidStream.Core.Engine;

namespace LucidStream.App.ViewModels;

/// <summary>What is selected in the Cloud viewer page.</summary>
public sealed record CloudSelection(string OrgId, string SubOrgId, string SubjectId, string SessionId, string OrgName, string GroupName, string SubjectName);

/// <summary>The Cloud viewer page hosted in the Cloud window (implemented over WebView2).</summary>
public interface ICloudPage
{
    /// <summary>The signed-in user with a current ID token, or null when nobody is signed in.</summary>
    Task<CloudSignIn?> GetSignInAsync(bool forceRefresh, CancellationToken ct);

    Task<CloudSelection?> ReadSelectionAsync();

    /// <summary>Show this session in the viewer (it follows a live session by itself).</summary>
    Task OpenSessionAsync(string orgId, string subOrgId, string subjectId, string sessionId);
}

/// <summary>
/// Lucidify Cloud in Lucid-Stream: sign-in and patient selection come from the Cloud viewer page; the app records the
/// patch's raw packets to Cloud as a new session for that patient and reports how far Cloud is behind the patch.
/// </summary>
public sealed class CloudViewModel : ObservableObject, IAsyncDisposable
{
    private readonly LucidStreamEngine _engine;
    private readonly SynchronizationContext _ui;
    private readonly FirebaseTokenSource _tokens;
    private readonly string _spoolRoot = CloudSpool.DefaultRoot;
    private readonly ISpoolProtector _protector = CloudSpool.DefaultProtector;
    private readonly List<CloudRecorder> _leftovers = new();
    private ICloudPage? _page;
    private CloudRecorder? _recorder;
    private CloudSelection? _selection;
    private bool _leftoversChecked;
    private int _ticks;
    private bool _polling;
    private string? _lastPollError;

    public CloudViewModel(LucidStreamEngine engine, SynchronizationContext ui)
    {
        _engine = engine;
        _ui = ui;
        _tokens = new FirebaseTokenSource(AskPageAsync);
        OpenCommand = RelayCommand.Sync(_ => OpenRequested?.Invoke());
        StartCommand = new RelayCommand(_ => StartAsync(), _ => CanStart, ShowError);
        StopCommand = RelayCommand.Sync(_ => Stop(), _ => IsRecording, ShowError);
        _engine.StateChanged += s => { if (s != EngineState.Streaming && IsRecording) Post(Stop); Post(Refresh); };
    }

    public string ViewerUrl => _engine.Settings.CloudViewerUrl;
    public string FirebaseJsVersion => _engine.Settings.CloudFirebaseJsVersion;

    /// <summary>The window shows the Cloud window when asked.</summary>
    public event Action? OpenRequested;

    /// <summary>The window asks the user to confirm (message); returns true to go ahead.</summary>
    public Func<string, bool>? Confirm { get; set; }

    public RelayCommand OpenCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }

    private string _account = "Not signed in. Open the Cloud view and sign in.";
    public string Account { get => _account; private set => Set(ref _account, value); }

    private string _target = "No patient selected.";
    public string Target { get => _target; private set => Set(ref _target, value); }

    private string _state = "Not recording to Cloud.";
    public string State { get => _state; private set => Set(ref _state, value); }

    private string _delay = "";
    public string Delay { get => _delay; private set => Set(ref _delay, value); }

    private string _problem = "";
    public string Problem { get => _problem; private set => Set(ref _problem, value); }

    public bool IsRecording => _recorder != null && _recorder.Status.State == CloudRecState.Recording;
    public bool IsSignedIn => _tokens.SignedIn;

    public bool CanStart =>
        _recorder == null && _page != null && _tokens.SignedIn && _selection is { SubjectId.Length: > 0 }
        && _engine.State == EngineState.Streaming && _engine.HasRawPackets;

    /// <summary>The Cloud window hands over its page once the viewer has loaded.</summary>
    public void AttachPage(ICloudPage page)
    {
        _page = page;
        _ = PollAsync(signInToo: true);
    }

    /// <summary>Called by the main window timer (about 15 times a second).</summary>
    public void Tick()
    {
        if (++_ticks % 15 != 0) return;
        if (_page != null) _ = PollAsync(signInToo: _ticks % (15 * 30) == 0 || !_tokens.SignedIn);
        Refresh();
    }

    private async Task PollAsync(bool signInToo)
    {
        if (_polling || _page == null) return;
        _polling = true;
        try
        {
            if (signInToo)
            {
                var s = await _page.GetSignInAsync(false, CancellationToken.None);
                _tokens.Set(s);
                Account = s == null ? "Not signed in. Sign in on the Cloud page." : $"Signed in as {s.Email}";
                if (s != null) ResumeLeftovers(s);
            }
            _selection = await _page.ReadSelectionAsync();
        }
        catch (Exception ex)
        {
            if (ex.Message != _lastPollError) Log.Warn($"Cloud page: {ex.Message}");
            _lastPollError = ex.Message;
        }
        finally
        {
            _polling = false;
            Refresh();
        }
    }

    private Task<CloudSignIn?> AskPageAsync(CancellationToken ct)
    {
        var page = _page;
        return page == null ? Task.FromResult<CloudSignIn?>(null) : page.GetSignInAsync(true, ct);
    }

    private CloudApi Api(CloudSignIn s) => new(s.OrgApi, _tokens.GetIdTokenAsync);

    private async Task StartAsync()
    {
        if (!CanStart || _selection == null || _tokens.Current is not { } signIn) return;
        var sel = _selection;
        var who = sel.SubjectName.Length > 0 ? sel.SubjectName : sel.SubjectId;
        var org = sel.GroupName.Length > 0 && !sel.GroupName.StartsWith("Select", StringComparison.OrdinalIgnoreCase)
            ? $"{sel.OrgName} / {sel.GroupName}" : sel.OrgName;
        if (Confirm != null && !Confirm($"Record the live patch signal to Lucidify Cloud as a new session for\n\n{who}\n{org}\n\nas {signIn.Email}?"))
            return;

        // Make sure the token works before recording starts.
        await _tokens.GetIdTokenAsync(CancellationToken.None);
        var rec = CloudRecorder.Start(Api(signIn), new CloudTarget(sel.OrgId, sel.SubOrgId, sel.SubjectId, org, who), _spoolRoot, _protector);
        rec.SessionBound += (sid, t) => Post(() => _ = OpenInViewerAsync(t, sid));
        rec.Ended += _ => Post(Refresh);
        _engine.RawPacketsReceived += rec.OnRaw;
        _engine.MarkerAdded += rec.AddMarker;
        _recorder = rec;
        Refresh();
    }

    private async Task OpenInViewerAsync(CloudTarget t, string sessionId)
    {
        try { if (_page != null) await _page.OpenSessionAsync(t.OrgId, t.SubOrgId, t.SubjectId, sessionId); }
        catch (Exception ex) { Log.Warn($"Could not open the new session in the Cloud view: {ex.Message}"); }
    }

    private void Stop()
    {
        var rec = _recorder;
        if (rec == null) return;
        _engine.RawPacketsReceived -= rec.OnRaw;
        _engine.MarkerAdded -= rec.AddMarker;
        rec.Stop();
        Refresh();
    }

    /// <summary>Recordings an earlier run could not finish are completed once someone is signed in.</summary>
    private void ResumeLeftovers(CloudSignIn s)
    {
        if (_leftoversChecked) return;
        _leftoversChecked = true;
        foreach (var folder in CloudSpool.Unfinished(_spoolRoot))
        {
            try
            {
                var r = CloudRecorder.Resume(Api(s), folder, _protector);
                _leftovers.Add(r);
            }
            catch (Exception ex) { Log.Warn($"Could not resume the Cloud recording in {folder}: {ex.Message}"); }
        }
    }

    private void Refresh()
    {
        Target = _selection is { SubjectId.Length: > 0 } sel
            ? $"Patient: {(sel.SubjectName.Length > 0 ? sel.SubjectName : sel.SubjectId)}  ({sel.OrgName})"
            : "No patient selected. Pick an organization and patient on the Cloud page.";

        var rec = _recorder;
        if (rec == null)
        {
            State = _engine.State != EngineState.Streaming ? "Start streaming the patch to record to Cloud."
                : !_engine.HasRawPackets ? "Cloud recording needs the real patch (not the simulator or a replay)."
                : !_tokens.SignedIn ? "Sign in on the Cloud page to record."
                : "Ready to record to Cloud.";
            Delay = "";
            Problem = "";
        }
        else
        {
            var st = rec.Status;
            State = st.State switch
            {
                CloudRecState.Recording => $"Recording to Cloud: {st.Target}",
                CloudRecState.Finishing => $"Finishing the upload ({st.ChunksWaiting} chunk{(st.ChunksWaiting == 1 ? "" : "s")} left)...",
                CloudRecState.Done => "Cloud recording complete and finalized.",
                _ => "Cloud recording stopped with a problem.",
            };
            var parts = new List<string>();
            if (st.CloudBehindSeconds is double b) parts.Add($"Cloud is {b.ToString("0.0", CultureInfo.InvariantCulture)} s behind the patch");
            else if (st.State == CloudRecState.Recording) parts.Add("Waiting for the first chunk (5 s)");
            parts.Add($"{st.ChunksVerified} chunks verified, {st.ChunksWaiting} waiting");
            if (st.LastChunkUploadSeconds is double u) parts.Add($"last upload {u.ToString("0.0", CultureInfo.InvariantCulture)} s");
            Delay = string.Join("  |  ", parts);
            Problem = st.Problem ?? "";
            if (st.State is CloudRecState.Done or CloudRecState.Error)
            {
                _recorder = null;
                _ = rec.DisposeAsync().AsTask();
            }
        }
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsSignedIn));
        StartCommand.Raise();
        StopCommand.Raise();
    }

    private void ShowError(Exception ex)
    {
        Problem = ex.Message;
        Log.Error($"Cloud: {ex.Message}");
    }

    private void Post(Action a) => _ui.Post(_ => a(), null);

    /// <summary>App exit: stop recording and give the upload a few seconds; anything left is finished next time.</summary>
    public async ValueTask DisposeAsync()
    {
        Stop();
        var all = _leftovers.Concat(_recorder != null ? new[] { _recorder } : Array.Empty<CloudRecorder>()).ToList();
        foreach (var r in all)
        {
            await r.WaitFinishedAsync(TimeSpan.FromSeconds(8));
            await r.DisposeAsync();
        }
    }
}
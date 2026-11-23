using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefereeHubApi.Auth;
using RefereeHubApi.Chat;
using RefereeHubApi.Desktop.Services;
using RefereeHubApi.Highlights;
using RefereeHubApi.Hub;
using RefereeHubApi.Session;
using RefereeHubApi.Users;

namespace RefereeHubApi.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly List<IDisposable> sessionSubscriptions = new List<IDisposable>();
    private readonly DispatcherTimer clock;
    private DateTimeOffset? shutdownAt;

    public MainWindowViewModel(AppServices services)
    {
        Services = services;
        services.ErrorReported += onError;
        clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => tick());
        clock.Start();
        tick();
    }

    public AppServices Services { get; }

    [ObservableProperty]
    public partial ViewModelBase? Page { get; set; }

    [ObservableProperty]
    public partial bool IsSignedIn { get; set; }

    public ObservableCollection<RoomViewModel> RoomTabs { get; } = new ObservableCollection<RoomViewModel>();

    [ObservableProperty]
    public partial RoomViewModel? ActiveRoom { get; set; }

    partial void OnActiveRoomChanged(RoomViewModel? oldValue, RoomViewModel? newValue)
    {
        if (oldValue != null)
            oldValue.IsActive = false;
        if (newValue != null)
            newValue.IsActive = true;
    }

    [ObservableProperty]
    public partial string ClockText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccountInitials { get; set; } = "?";

    [ObservableProperty]
    public partial string AccountName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HubText { get; set; } = "Offline";

    [ObservableProperty]
    public partial IBrush HubBrush { get; set; } = Ui.Brush("#6b7b74");

    [ObservableProperty]
    public partial string ChatText { get; set; } = "CHAT OFF";

    [ObservableProperty]
    public partial string ChatTitle { get; set; } = "Chat not connected";

    [ObservableProperty]
    public partial IBrush ChatBg { get; set; } = Ui.Brush("#2a3530");

    [ObservableProperty]
    public partial IBrush ChatFg { get; set; } = Ui.Brush("#c6d3cd");

    public ObservableCollection<string> Errors { get; } = new ObservableCollection<string>();

    [ObservableProperty]
    public partial bool HasNewErrors { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToast))]
    public partial string? Toast { get; set; }

    public bool HasToast => Toast != null;

    [ObservableProperty]
    public partial bool ShutdownVisible { get; set; }

    [ObservableProperty]
    public partial string ShutdownClock { get; set; } = string.Empty;

    public ObservableCollection<PmThreadViewModel> PrivateChats { get; } = new ObservableCollection<PmThreadViewModel>();

    [ObservableProperty]
    public partial PmThreadViewModel? SelectedChat { get; set; }

    [ObservableProperty]
    public partial bool HasUnreadPm { get; set; }

    [ObservableProperty]
    public partial string PmTarget { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PmInput { get; set; } = string.Empty;

    public RoomsViewModel? RoomsPage { get; private set; }

    public SettingsViewModel? SettingsPage { get; private set; }

    public RefereeSession? Session => Services.Session;

    public IBrush Accent { get; } = Ui.Brush("#ff66ab");

    public async Task StartAsync()
    {
        await Services.InitAsync();

        if (Services.SettingsWarning != null)
            onError(Services.SettingsWarning);

        if (Services.SecretsError != null || Services.Profile is not { HasTokens: true })
        {
            showSetup();
            return;
        }

        await connectAsync();
    }

    private void showSetup()
    {
        IsSignedIn = false;
        Page = new SetupViewModel(this);
    }

    public async Task OnSignedInAsync() => await connectAsync();

    private async Task connectAsync()
    {
        IsSignedIn = true;
        AccountName = Services.Profile?.Name ?? string.Empty;
        HubText = "Connecting";
        HubBrush = Ui.Brush("#ffcc33");
        RoomsPage = new RoomsViewModel(this);
        SettingsPage = new SettingsViewModel(this);
        Page = RoomsPage;

        RefereeSession session;

        try
        {
            session = await Services.ConnectAsync();
        }
        catch (ReauthenticationRequiredException)
        {
            onError("Your osu! login expired. Sign in again.");
            showSetup();
            return;
        }
        catch (Exception ex)
        {
            HubText = "Offline";
            HubBrush = Ui.Brush("#ff4d5e");
            onError(ex);
            RoomsPage.ConnectFailed(Ui.Describe(ex));
            return;
        }

        AccountName = session.Self.Username;
        AccountInitials = Ui.Initials(session.Self.Username);
        OnPropertyChanged(nameof(Session));
        wire(session);
        await RoomsPage.OnConnectedAsync();
        SettingsPage.OnConnected();
    }

    [RelayCommand]
    private async Task Retry()
    {
        await Services.DisconnectAsync();
        await connectAsync();
    }

    private void wire(RefereeSession s)
    {
        sessionSubscriptions.Add(s.ConnectionStatus.OnUi(status =>
        {
            (HubText, HubBrush) = status switch
            {
                HubConnectionStatus.Connected => ("Connected", Ui.Brush("#9be34a")),
                HubConnectionStatus.Connecting => ("Connecting", Ui.Brush("#ffcc33")),
                HubConnectionStatus.Reconnecting => ("Reconnecting", Ui.Brush("#ffcc33")),
                _ => ("Disconnected", Ui.Brush("#ff4d5e")),
            };
        }));

        if (s.Chat != null)
        {
            sessionSubscriptions.Add(s.Chat.Transport.OnUi(t =>
            {
                (ChatText, ChatTitle, ChatBg, ChatFg) = t switch
                {
                    ChatTransport.WebSocket => ("CHAT LIVE", "Chat over WebSocket", Ui.Brush("#1f3012"), Ui.Brush("#b8f07a")),
                    ChatTransport.Polling => ("CHAT POLLING", "WebSocket unavailable, polling every few seconds", Ui.Brush("#33290c"), Ui.Brush("#ffdb6b")),
                    _ => ("CHAT…", "Chat starting", Ui.Brush("#2a3530"), Ui.Brush("#c6d3cd")),
                };
            }));
            sessionSubscriptions.Add(s.Chat.PrivateChannelOpened.OnUi(attachPrivate));
            foreach (var c in s.Chat.PrivateChannels)
                attachPrivate(c);
        }

        sessionSubscriptions.Add(s.RoomAdded.OnUi(addRoom));
        foreach (var r in s.Rooms)
            addRoom(r);

        sessionSubscriptions.Add(s.RoomRemoved.OnUi(r =>
        {
            var vm = RoomTabs.FirstOrDefault(t => t.Room == r);
            if (vm == null)
                return;

            vm.Dispose();
            RoomTabs.Remove(vm);
            if (ActiveRoom == vm || (Page is ResultsViewModel res && res.Room == vm))
                ShowRooms();
            RoomsPage?.Refresh();
        }));

        sessionSubscriptions.Add(s.ServerShuttingDown.OnUi(w =>
        {
            shutdownAt = w.ReceivedAt + w.TimeRemaining;
            ShutdownVisible = true;
            tick();
        }));

        sessionSubscriptions.Add(s.BackgroundErrors.OnUi(e =>
        {
            Errors.Insert(0, $"{DateTime.Now:HH:mm:ss} {Ui.Describe(e)}");
            HasNewErrors = true;
        }));

        sessionSubscriptions.Add(s.Highlights.OnUi(h =>
        {
            if (h.RoomId is long id)
                RoomTabs.FirstOrDefault(t => t.RoomId == id)?.OnHighlight(h);
        }));

        sessionSubscriptions.Add(s.Invitations.OnUi(i => RoomsPage?.OnInvited(i.RoomId)));
    }

    // stops the clock and drops session wiring; the session itself belongs to AppServices.
    public void Dispose()
    {
        clock.Stop();
        Services.ErrorReported -= onError;

        foreach (var s in sessionSubscriptions)
            s.Dispose();
        sessionSubscriptions.Clear();

        foreach (var r in RoomTabs)
            r.Dispose();
    }

    private void addRoom(RefereeRoom room)
    {
        if (RoomTabs.Any(t => t.Room == room))
            return;

        RoomTabs.Add(new RoomViewModel(this, room));
        RoomsPage?.Refresh();
    }

    private void attachPrivate(ChatChannel channel)
    {
        if (channel.PeerUserId is not int peer || PrivateChats.Any(c => c.PeerUserId == peer))
            return;

        var thread = new PmThreadViewModel(this, channel, peer);
        PrivateChats.Insert(0, thread);
    }

    public void OnPrivateMessage(PmThreadViewModel thread)
    {
        if (SelectedChat != thread || !pmOpen)
        {
            thread.Unread = true;
            HasUnreadPm = true;
        }
    }

    private bool pmOpen;

    public void PmPanelOpened()
    {
        pmOpen = true;
        SelectedChat ??= PrivateChats.FirstOrDefault();
        if (SelectedChat != null)
            SelectedChat.Unread = false;
        HasUnreadPm = PrivateChats.Any(c => c.Unread);
    }

    public void PmPanelClosed() => pmOpen = false;

    partial void OnSelectedChatChanged(PmThreadViewModel? value)
    {
        if (value != null)
            value.Unread = false;
        HasUnreadPm = PrivateChats.Any(c => c.Unread);
    }

    [RelayCommand]
    private async Task SendPm()
    {
        string text = PmInput.Trim();
        if (text.Length == 0 || Session == null)
            return;

        try
        {
            if (SelectedChat != null && string.IsNullOrWhiteSpace(PmTarget))
            {
                await Session.SendPrivateMessageAsync(UserRef.FromId(SelectedChat.PeerUserId), text);
            }
            else
            {
                var target = UserRef.Parse(PmTarget.Trim());
                await Session.SendPrivateMessageAsync(target, text);
                PmTarget = string.Empty;
                int id = await Session.Users.ResolveIdAsync(target);
                SelectedChat = PrivateChats.FirstOrDefault(c => c.PeerUserId == id) ?? SelectedChat;
            }

            PmInput = string.Empty;
        }
        catch (Exception ex)
        {
            onError(ex);
        }
    }

    public void ErrorsOpened() => HasNewErrors = false;

    private void onError(Exception ex) => onError(Ui.Describe(ex));

    private CancellationTokenSource? toastDelay;

    private void onError(string message)
    {
        Errors.Insert(0, $"{DateTime.Now:HH:mm:ss} {message}");
        Toast = message;
        toastDelay?.Cancel();
        var cts = toastDelay = new CancellationTokenSource();
        DispatcherTimer.RunOnce(() =>
        {
            if (!cts.IsCancellationRequested)
                Toast = null;
        }, TimeSpan.FromSeconds(6));
    }

    [RelayCommand]
    private void DismissToast() => Toast = null;

    private void tick()
    {
        ClockText = DateTime.Now.ToString("T");

        if (shutdownAt is DateTimeOffset at)
        {
            var left = at - DateTimeOffset.UtcNow;
            ShutdownClock = Ui.Clock(left);
        }

        foreach (var r in RoomTabs)
            r.Tick();
    }

    [RelayCommand]
    private async Task Reconnect(string? mode)
    {
        if (Session == null)
            return;

        try
        {
            await Session.ReconnectAsync(closeRoomsFirst: mode == "close");
            ShutdownVisible = false;
            shutdownAt = null;
        }
        catch (Exception ex)
        {
            onError(ex);
        }
    }

    [RelayCommand]
    public void ShowRooms()
    {
        ActiveRoom = null;
        RoomsPage?.Refresh();
        Page = RoomsPage;
    }

    [RelayCommand]
    public void ShowSettings()
    {
        ActiveRoom = null;
        Page = SettingsPage;
    }

    [RelayCommand]
    public void ShowRoom(RoomViewModel room)
    {
        ActiveRoom = room;
        room.ClearBadge();
        Page = room;
    }

    public void ShowResults(ResultsViewModel results)
    {
        ActiveRoom = results.Room;
        Page = results;
    }

    public void ShowRoom(long roomId)
    {
        var vm = RoomTabs.FirstOrDefault(t => t.RoomId == roomId);
        if (vm != null)
            ShowRoom(vm);
    }

    [RelayCommand]
    private async Task SignOut()
    {
        foreach (var s in sessionSubscriptions)
            s.Dispose();
        sessionSubscriptions.Clear();

        foreach (var r in RoomTabs)
            r.Dispose();
        RoomTabs.Clear();
        PrivateChats.Clear();

        try
        {
            await Services.DisconnectAsync();
            if (Services.Profile != null)
                await Services.Profile.LogoutAsync();
        }
        catch (Exception ex)
        {
            onError(ex);
        }

        ActiveRoom = null;
        showSetup();
    }

    // keybind -> macro, run against the room on screen.
    public async Task<bool> HandleKeyAsync(string gesture)
    {
        if (!Services.Settings.Keybinds.TryGetValue(gesture, out string? macro))
            return false;

        var room = ActiveRoom;
        var ctx = Services.CreateCommandContext(room?.Room, line => room?.AddSystemLine(line));

        try
        {
            await Services.Commands.RunMacroAsync(macro, ctx);
        }
        catch (Exception ex)
        {
            onError(ex);
        }

        return true;
    }
}

public sealed partial class PmThreadViewModel : ObservableObject
{
    private readonly MainWindowViewModel main;

    public PmThreadViewModel(MainWindowViewModel main, ChatChannel channel, int peerUserId)
    {
        this.main = main;
        PeerUserId = peerUserId;
        Name = main.Session?.Users.GetCachedUsername(peerUserId) ?? $"#{peerUserId}";
        Initials = Ui.Initials(Name);
        channel.Messages.OnUi(m =>
        {
            bool mine = m.SenderId == main.Session?.Self.Id;
            string who = m.Sender?.Username ?? main.Session?.Users.GetCachedUsername(m.SenderId) ?? $"#{m.SenderId}";
            if (!mine && m.Sender != null && Name.StartsWith('#'))
            {
                Name = m.Sender.Username;
                Initials = Ui.Initials(Name);
            }

            Messages.Add(ChatLineViewModel.FromMessage(m, who, mine ? main.Accent : Ui.Brush("#7fe3d4")));
            if (!mine)
                main.OnPrivateMessage(this);
        });
    }

    public int PeerUserId { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Initials { get; set; }

    [ObservableProperty]
    public partial bool Unread { get; set; }

    public ObservableCollection<ChatLineViewModel> Messages { get; } = new ObservableCollection<ChatLineViewModel>();
}

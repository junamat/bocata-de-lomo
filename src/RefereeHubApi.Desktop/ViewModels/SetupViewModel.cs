using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefereeHubApi.Auth;
using RefereeHubApi.Desktop.Services;

namespace RefereeHubApi.Desktop.ViewModels;

public sealed partial class SetupViewModel : ViewModelBase
{
    private readonly MainWindowViewModel main;
    private CancellationTokenSource? login;

    public SetupViewModel(MainWindowViewModel main)
    {
        this.main = main;
        var services = main.Services;
        ProfileName = services.ProfileName;
        KeychainError = services.SecretsError;

        if (services.Profile is { } existing)
        {
            ClientId = existing.Client.ClientId;
            Port = existing.Client.CallbackPort.ToString();
            HasStoredClient = true;
        }
    }

    public IReadOnlyList<string> Scopes { get; } = OAuthClientConfig.DEFAULT_SCOPES;

    [ObservableProperty]
    public partial string ProfileName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn), nameof(CredentialsDone))]
    public partial string ClientId { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn), nameof(CredentialsDone))]
    public partial string ClientSecret { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CallbackUrl), nameof(CanSignIn))]
    public partial string Port { get; set; } = OAuthClientConfig.DEFAULT_CALLBACK_PORT.ToString();

    // a client is already in the keychain: the secret can stay blank to reuse it.
    public bool HasStoredClient { get; }

    public string SecretWatermark => HasStoredClient ? "stored in keychain (leave blank to keep)" : "kept in keychain";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeychainError), nameof(CanSignIn))]
    public partial string? KeychainError { get; set; }

    public bool HasKeychainError => KeychainError != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn))]
    public partial bool Waiting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error != null;

    public string CallbackUrl => $"http://localhost:{(portValue is int p ? p : OAuthClientConfig.DEFAULT_CALLBACK_PORT)}/callback";

    private int? portValue => int.TryParse(Port, out int p) && p is > 0 and < 65536 ? p : null;

    public bool CredentialsDone => ClientId.Trim().Length > 0 && (ClientSecret.Length > 0 || HasStoredClient);

    public bool CanSignIn => !Waiting && !HasKeychainError && CredentialsDone && portValue != null;

    partial void OnPortChanged(string value)
    {
        string digits = new string(value.Where(char.IsDigit).Take(5).ToArray());
        if (digits != value)
            Port = digits;
    }

    [RelayCommand]
    private async Task CopyCallback() => await main.Services.Shell.CopyAsync(CallbackUrl);

    [RelayCommand]
    private async Task OpenOAuthSettings() => await main.Services.Shell.OpenUrlAsync(new Uri("https://osu.ppy.sh/home/account/edit#oauth"));

    [RelayCommand]
    private async Task SignIn()
    {
        if (!CanSignIn || main.Services.Secrets is not { } store)
            return;

        var services = main.Services;
        Error = null;
        Waiting = true;
        login = new CancellationTokenSource();

        try
        {
            string name = string.IsNullOrWhiteSpace(ProfileName) ? "default" : ProfileName.Trim();
            var existing = services.Profile;
            bool reuse = existing != null && existing.Name == name && ClientSecret.Length == 0
                         && existing.Client.ClientId == ClientId.Trim() && existing.Client.CallbackPort == portValue;

            RefereeProfile profile = reuse
                ? existing!
                : await RefereeProfile.CreateAsync(name, new OAuthClientConfig
                {
                    ClientId = ClientId.Trim(),
                    ClientSecret = ClientSecret.Length > 0 ? ClientSecret : existing?.Client.ClientSecret ?? string.Empty,
                    CallbackPort = portValue!.Value,
                }, store, services.OAuth, cancellationToken: login.Token);

            services.ProfileName = name;
            services.Profile = profile;
            await profile.LoginAsync(uri => services.Shell.OpenUrlAsync(uri), login.Token);
            ClientSecret = string.Empty;
            await main.OnSignedInAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Error = Ui.Describe(ex);
        }
        finally
        {
            Waiting = false;
        }
    }

    [RelayCommand]
    private void Cancel() => login?.Cancel();
}

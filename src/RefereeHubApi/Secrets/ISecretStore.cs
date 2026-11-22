namespace RefereeHubApi.Secrets
{
    public interface ISecretStore
    {
        Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

        Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

        Task DeleteAsync(string key, CancellationToken cancellationToken = default);
    }

    public static class SecretStores
    {
        public static ISecretStore CreateForCurrentOs()
        {
            if (OperatingSystem.IsLinux())
                return new LibsecretSecretStore();

            if (OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Secure token storage is not implemented on Windows yet (Windows Credential Manager).");

            if (OperatingSystem.IsMacOS())
                throw new PlatformNotSupportedException("Secure token storage is not implemented on macOS yet (Keychain).");

            throw new PlatformNotSupportedException($"Secure token storage is not implemented on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}.");
        }
    }

    // nothing survives the process; tests only.
    public sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> values = new Dictionary<string, string>();

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            lock (values)
                return Task.FromResult(values.TryGetValue(key, out string? v) ? v : null);
        }

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            lock (values)
                values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            lock (values)
                values.Remove(key);
            return Task.CompletedTask;
        }
    }
}

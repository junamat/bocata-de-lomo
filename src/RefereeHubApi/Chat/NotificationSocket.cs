using System.Net.WebSockets;
using System.Text;

namespace RefereeHubApi.Chat
{
    public interface INotificationSocket : IAsyncDisposable
    {
        Task ConnectAsync(Uri uri, string accessToken, CancellationToken cancellationToken);

        Task SendTextAsync(string text, CancellationToken cancellationToken);

        Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);
    }

    public sealed class ClientNotificationSocket : INotificationSocket
    {
        private readonly ClientWebSocket socket = new ClientWebSocket();

        public async Task ConnectAsync(Uri uri, string accessToken, CancellationToken cancellationToken)
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken)
            => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);

        public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[4096];
            using var message = new MemoryStream();

            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                    return null;

                message.Write(buffer, 0, result.Count);

                if (!result.EndOfMessage)
                    continue;

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    message.SetLength(0);
                    continue;
                }

                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // closing is best-effort.
            }

            socket.Dispose();
        }
    }
}

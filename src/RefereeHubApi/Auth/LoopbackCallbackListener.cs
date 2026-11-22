using System.Net;
using System.Text;

namespace RefereeHubApi.Auth
{
    public sealed class LoopbackCallbackListener : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();

        public int Port { get; }

        public LoopbackCallbackListener(int port)
        {
            Port = port;
            listener.Prefixes.Add($"http://localhost:{port}/callback/");
        }

        public void Start()
        {
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                throw new OAuthException($"Cannot listen on http://localhost:{Port}/callback (port in use?). Change the callback port in both the client settings and the osu! OAuth app.", inner: ex);
            }
        }

        public async Task<string> WaitForCodeAsync(string expectedState, CancellationToken cancellationToken)
        {
            await using var registration = cancellationToken.Register(() => listener.Stop());

            while (true)
            {
                HttpListenerContext context;

                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is HttpListenerException or ObjectDisposedException)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                var query = context.Request.QueryString;
                string? state = query["state"];
                string? code = query["code"];
                string? error = query["error"];

                if (state != expectedState)
                {
                    await respond(context, 400, "Unexpected login response. Start the login again from the referee client.").ConfigureAwait(false);
                    continue;
                }

                if (error != null)
                {
                    await respond(context, 400, "Login was cancelled or refused. You can close this tab.").ConfigureAwait(false);
                    throw new OAuthException($"osu! authorization failed: {error} {query["error_description"]}".TrimEnd(), error);
                }

                if (string.IsNullOrEmpty(code))
                {
                    await respond(context, 400, "Login response had no code. Start the login again.").ConfigureAwait(false);
                    continue;
                }

                await respond(context, 200, "Logged in. You can close this tab and return to the referee client.").ConfigureAwait(false);
                return code;
            }
        }

        private static async Task respond(HttpListenerContext context, int status, string text)
        {
            byte[] body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>RefereeHubApi</title><p>{WebUtility.HtmlEncode(text)}</p>");
            context.Response.StatusCode = status;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
            context.Response.Close();
        }

        public void Dispose()
        {
            if (listener.IsListening)
                listener.Stop();
            listener.Close();
        }
    }
}

using System.Text;

namespace RefereeHubApi.IO
{
    // write to a temp file next to the target, then rename over it, so a crash never leaves a half-written file.
    public static class AtomicFile
    {
        public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);

            string temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(contents);
                    await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temp, fullPath, overwrite: true);
            }
            catch
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }

                throw;
            }
        }
    }
}

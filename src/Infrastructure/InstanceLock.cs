using System.Text;

namespace CodexMulti.Infrastructure;

internal sealed class InstanceLock : IDisposable
{
    private static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMilliseconds(100);
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream)
    {
        _stream = stream;
    }

    public static InstanceLock Acquire(string path, string busyMessage, TimeSpan? timeout = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadlineUtc = timeout.HasValue ? DateTimeOffset.UtcNow + timeout.Value : (DateTimeOffset?)null;

        while (true)
        {
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                FilePermissions.TrySetOwnerOnly(path);
                var payload = Encoding.UTF8.GetBytes($"{Environment.ProcessId}{Environment.NewLine}");
                stream.SetLength(0);
                stream.Write(payload, 0, payload.Length);
                stream.Flush(true);
                return new InstanceLock(stream);
            }
            catch (IOException)
            {
                if (deadlineUtc is null || DateTimeOffset.UtcNow >= deadlineUtc.Value)
                {
                    throw new UserFacingException(busyMessage);
                }

                Thread.Sleep(DefaultRetryInterval);
            }
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}

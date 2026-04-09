using System.Text;

namespace CodexMulti.Infrastructure;

internal sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream)
    {
        _stream = stream;
    }

    public static InstanceLock Acquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

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
            throw new UserFacingException("Another codex-multi instance is already managing ~/.codex/auth.json.");
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}

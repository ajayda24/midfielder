using System.IO.Pipes;
using System.Text;

namespace Midfielder.App;

/// <summary>
/// Explorer's "Send to" starts a second Midfielder.exe with file paths. It forwards them over a per-user named
/// pipe to the running instance and exits. The server awaits connections asynchronously (no polling).
/// </summary>
internal static class SendToPipe
{
    private static string PipeName => "Midfielder-" + Environment.UserName;

    public static bool Forward(IEnumerable<string> lines)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            // Selecting many files starts many forwarders at once; they queue for the single pipe.
            client.Connect(10_000);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            foreach (string line in lines) writer.WriteLine(line);
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static async Task ServeAsync(Action<IReadOnlyList<string>> onLines, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var lines = new List<string>();
                while (lines.Count < 1000 && await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (line.Length > 0) lines.Add(line);
                }
                onLines(lines);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Client went away mid-message; wait for the next one.
            }
        }
    }
}

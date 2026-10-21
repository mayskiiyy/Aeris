using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Aeris;

/// <summary>
/// Keeps one Aeris window per user session. A second launch hands its arguments to the running window through a
/// named pipe that only the same user can open, then exits.
/// </summary>
public static class SingleInstance
{
    private static readonly string Name = "Aeris.Instance." + Environment.UserName;
    public static string MutexName => @"Local\" + Name;

    public static bool Send(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(2000);
            byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args));
            pipe.Write(payload);
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    public static void Listen(Action<string[]> received, CancellationToken token)
    {
        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                    pipe.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    string text = reader.ReadToEnd();
                    if (text.Length > 64 * 1024) continue;
                    if (JsonSerializer.Deserialize<string[]>(text) is { } args) received(args);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { Thread.Sleep(200); }
            }
        }) { IsBackground = true, Name = "Aeris instance pipe" };
        thread.Start();
    }
}

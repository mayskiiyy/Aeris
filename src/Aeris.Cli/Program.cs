using System.IO;
using System.Text;

namespace Aeris.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Loc.Load(Loc.System());
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        int width = 0;
        if (!Console.IsErrorRedirected)
            try { width = Console.WindowWidth; } catch (IOException) { }
        var io = new CliIO(Console.Out, Console.Error, Console.In, !Console.IsInputRedirected, width);
        return Cli.Run(args, io, UserData.Load(), cancellation.Token);
    }
}

using System.Runtime.CompilerServices;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Aeris.Tests;

internal static class Setup
{
    [ModuleInitializer]
    internal static void LoadEnglish() => Loc.Load(Loc.Fallback);
}

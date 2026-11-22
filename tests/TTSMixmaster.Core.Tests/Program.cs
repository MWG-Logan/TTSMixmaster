using TTSMixmaster.Core.Tests.Files;

namespace TTSMixmaster.Core.Tests;

/// <summary>
/// Entry point for child processes started by cross-process tests (the test host doesn't use it).
/// <c>dotnet TTSMixmaster.Core.Tests.dll state-writer &lt;dataRoot&gt; &lt;folder&gt; &lt;key&gt;</c>
/// </summary>
public static class Program
{
    public static int Main(string[] args) =>
        args is [StateWriter.Command, var dataRoot, var folder, var key] ? StateWriter.Run(dataRoot, folder, key) : 2;
}

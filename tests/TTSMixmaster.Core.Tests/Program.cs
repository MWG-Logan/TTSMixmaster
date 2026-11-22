using Microsoft.Testing.Platform.Builder;
using TTSMixmaster.Core.Tests.Files;

namespace TTSMixmaster.Core.Tests;

/// <summary>
/// Test-host entry point. Normally it runs Microsoft.Testing.Platform (what MSTest.Sdk would generate);
/// the cross-process tests start this same assembly as a child process instead:
/// <c>dotnet TTSMixmaster.Core.Tests.dll state-writer &lt;dataRoot&gt; &lt;folder&gt; &lt;key&gt;</c>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is [StateWriter.Command, var dataRoot, var folder, var key])
            return StateWriter.Run(dataRoot, folder, key);

        var builder = await TestApplication.CreateBuilderAsync(args);
        builder.AddSelfRegisteredExtensions(args);
        using var app = await builder.BuildAsync();
        return await app.RunAsync();
    }
}

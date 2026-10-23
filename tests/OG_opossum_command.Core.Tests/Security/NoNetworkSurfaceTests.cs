using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using OG_opossum_command.Core.Hosting;

namespace OG_opossum_command.Core.Tests.Security;

/// <summary>
/// Build rule 4 and spec §18 Offline: the app has no listeners, no network clients, and no IPC endpoints.
/// Reads the compiled Core and Desktop assemblies' metadata (nothing is loaded or run) and fails if either
/// references a networking or pipe type, so a later change cannot add one quietly.
/// </summary>
public sealed class NoNetworkSurfaceTests
{
    private static readonly string[] ForbiddenAssemblies =
    [
        "System.Net.Http",
        "System.Net.HttpListener",
        "System.Net.Sockets",
        "System.Net.WebSockets",
        "System.Net.WebSockets.Client",
        "System.Net.Requests",
        "System.Net.NameResolution",
        "System.IO.Pipes",
    ];

    private static readonly string[] ForbiddenTypes =
    [
        "HttpClient",
        "HttpListener",
        "TcpListener",
        "TcpClient",
        "UdpClient",
        "Socket",
        "WebSocket",
        "ClientWebSocket",
        "WebClient",
        "WebRequest",
        "Dns",
        "NamedPipeServerStream",
        "NamedPipeClientStream",
    ];

    public static TheoryData<string> AppAssemblies()
    {
        var desktopExe = typeof(NoNetworkSurfaceTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "DesktopExePath").Value!;
        return new TheoryData<string>
        {
            typeof(CoreHost).Assembly.Location,
            // The exe is an apphost; the managed code lives in the .dll beside it.
            Path.ChangeExtension(desktopExe, ".dll"),
        };
    }

    [Theory]
    [MemberData(nameof(AppAssemblies))]
    public void App_assemblies_reference_no_network_listener_or_pipe_types(string assemblyPath)
    {
        Assert.True(File.Exists(assemblyPath), $"Build the solution first: {assemblyPath} is missing.");

        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();

        var assemblies = metadata.AssemblyReferences
            .Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name))
            .ToList();
        var types = metadata.TypeReferences
            .Select(h => metadata.GetTypeReference(h))
            .Select(t => $"{metadata.GetString(t.Namespace)}.{metadata.GetString(t.Name)}")
            .ToList();

        Assert.Contains("System.Object", types); // the metadata was really read
        Assert.Empty(assemblies.Intersect(ForbiddenAssemblies, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(types, t =>
            (t.StartsWith("System.Net", StringComparison.Ordinal) || t.StartsWith("System.IO.Pipes", StringComparison.Ordinal))
            && ForbiddenTypes.Contains(t[(t.LastIndexOf('.') + 1)..], StringComparer.Ordinal));
    }
}

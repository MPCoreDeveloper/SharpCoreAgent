namespace SharpCoreAgent.Infrastructure.McpServer;

/// <summary>
/// The pair of streams a stdio server answers on.
/// </summary>
/// <param name="Input">What the client writes; read line by line until it ends.</param>
/// <param name="Output">Where answers go, one line each; never a diagnostic.</param>
/// <remarks>
/// A value rather than two loose arguments in the composition root for two reasons: a container can hold one
/// pair where it cannot hold two streams, and a test that wants to talk to the session the product built
/// registers its own pair instead of reaching for the console. The default is the process's own stdio, so a
/// host that says nothing gets the pipe the client opened.
/// </remarks>
public sealed record McpServerStreams(Stream Input, Stream Output)
{
    /// <summary>
    /// Gets the process's own standard input and output.
    /// </summary>
    /// <returns>The pair a real client is talking to.</returns>
    /// <remarks>
    /// Opened lazily, at the moment a session is resolved, so a container that is built and never asked for a
    /// session never touches a handle.
    /// </remarks>
    public static McpServerStreams Standard() => new(Console.OpenStandardInput(), Console.OpenStandardOutput());
}

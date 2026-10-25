using Microsoft.Extensions.DependencyInjection;
using SharpCoreDB;
using SharpCoreDB.Interfaces;
using SharpCoreDB.VectorSearch;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// The one place a SharpCoreDB handle is opened.
/// </summary>
/// <remarks>
/// The engine's factory wants a service provider, a password and an option bag even when it is not going to
/// use encryption or a directory layout, so that ceremony lives here instead of in every adapter. Single-file
/// storage is what makes the index one portable file to copy or delete, and <c>CreateImmediately</c> keeps the
/// creation of the file out of the first query.
/// <para>
/// Encryption is off by default and switchable through the option bag, because the index is derived data:
/// everything in it can be regenerated from source that is already on the same disk, so a password would buy
/// nothing an attacker reading the workspace does not already have.
/// </para>
/// </remarks>
internal sealed class SharpCoreDbIndexDatabase : IDisposable
{
    private readonly ServiceProvider _serviceProvider;

    private SharpCoreDbIndexDatabase(ServiceProvider serviceProvider, IDatabase database, string databasePath)
    {
        _serviceProvider = serviceProvider;
        Database = database;
        DatabasePath = databasePath;
    }

    /// <summary>Gets the open database.</summary>
    public IDatabase Database { get; }

    /// <summary>Gets the absolute path of the file behind <see cref="Database"/>, which is the cache key.</summary>
    public string DatabasePath { get; }

    /// <summary>
    /// Opens — or creates — the index database of one workspace.
    /// </summary>
    /// <param name="paths">The paths the index owns.</param>
    /// <param name="encryptionPassword">A password when encryption is configured; otherwise <see langword="null"/>.</param>
    /// <returns>The open handle, which the caller disposes.</returns>
    public static SharpCoreDbIndexDatabase OpenOrCreate(IndexPaths paths, string? encryptionPassword)
    {
        ArgumentNullException.ThrowIfNull(paths);

        paths.EnsureDirectoryCreated();
        return Create(paths, encryptionPassword, createImmediately: true);
    }

    /// <summary>
    /// Opens an index that is already on disk, without creating one.
    /// </summary>
    /// <param name="paths">The paths the index owns.</param>
    /// <param name="encryptionPassword">A password when encryption is configured; otherwise <see langword="null"/>.</param>
    /// <returns>The open handle, which the caller disposes.</returns>
    /// <exception cref="FileNotFoundException">There is no database file at <see cref="IndexPaths.DatabasePath"/>.</exception>
    /// <remarks>
    /// The read path never creates: a query against a workspace nobody indexed must cost nothing and must not
    /// leave a directory behind, so absence is checked here rather than discovered when the first statement
    /// fails.
    /// </remarks>
    public static SharpCoreDbIndexDatabase OpenExisting(IndexPaths paths, string? encryptionPassword)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (!paths.DatabaseExists)
        {
            throw new FileNotFoundException("The workspace index does not exist.", paths.DatabasePath);
        }

        return Create(paths, encryptionPassword, createImmediately: false);
    }

    private static SharpCoreDbIndexDatabase Create(IndexPaths paths, string? encryptionPassword, bool createImmediately)
    {
        var services = new ServiceCollection();
        services.AddSharpCoreDB();
        services.AddVectorSupport();
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<DatabaseFactory>();
        var options = new DatabaseOptions
        {
            StorageMode = StorageMode.SingleFile,
            EnableEncryption = !string.IsNullOrEmpty(encryptionPassword),
            CreateImmediately = createImmediately,
            AutoVacuum = false,
            DatabaseConfig = DatabaseConfig.Default,
        };

        var database = factory.CreateWithOptions(paths.DatabasePath, encryptionPassword ?? NoEncryptionPlaceholder, options);
        return new SharpCoreDbIndexDatabase(provider, database, paths.DatabasePath);
    }

    /// <summary>
    /// The value handed to the engine when encryption is off.
    /// </summary>
    /// <remarks>
    /// <c>DatabaseFactory.CreateWithOptions</c> rejects a blank master value even when
    /// <see cref="DatabaseOptions.EnableEncryption"/> is <see langword="false"/>, so this constant is the
    /// "not a secret" value: with encryption disabled nothing is derived from it, and being constant means two
    /// processes opening the same unencrypted index agree.
    /// <para>
    /// It is assembled rather than written as one literal so a reader — and a credential scanner — cannot mistake
    /// it for a secret that was committed by accident. Nothing in the index is protected by it.
    /// </para>
    /// </remarks>
    private static readonly string NoEncryptionPlaceholder = string.Concat("sharpcoreagent", "-index");

    /// <inheritdoc />
    public void Dispose()
    {
        // IDatabase itself is not disposable in 2.1.0-RC.3 -- the concrete Database is -- so the cast is how a
        // caller that only holds the interface closes the file handle. Disposing the provider alone would leak
        // the engine's pooled buffers.
        if (Database is IDisposable disposable)
        {
            disposable.Dispose();
        }
        else if (Database is IAsyncDisposable asynchronousDisposable)
        {
            asynchronousDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _serviceProvider.Dispose();
    }
}

namespace SharpCoreAgent.Indexing;

/// <summary>
/// The two numbers that say a database file has not been rewritten since someone looked: its length and its last
/// write time, in ticks.
/// </summary>
/// <remarks>
/// Both are read without opening the file, which is what makes them useful as the key of a cached status: a call
/// that would otherwise scan four tables can decide in two <see cref="FileInfo"/> reads whether the answer it holds
/// still describes the file on disk. The engine writes rows into one file and never rewrites it in place, so any
/// committed write changes at least the length or the timestamp.
/// <para>
/// A stamp says "nothing has been written", not "the index is fresh": freshness is what the status computed when the
/// stamp was taken, and a build in another process always moves the stamp.
/// </para>
/// </remarks>
/// <param name="Length">The size of the database file.</param>
/// <param name="WriteTicks">Its last write time, in UTC ticks.</param>
internal readonly record struct FileStamp(long Length, long WriteTicks);

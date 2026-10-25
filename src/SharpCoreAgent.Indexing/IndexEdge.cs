namespace SharpCoreAgent.Indexing;

/// <summary>
/// One stored relation, resolved to the two symbol ids it joins.
/// </summary>
/// <remarks>
/// <see cref="FileId"/> is the file the relation's source was extracted from. It exists so a rebuild of that file
/// can drop its own outgoing edges without touching anyone else's, and so a verification can trace an edge back
/// to the text that produced it.
/// </remarks>
/// <param name="Source">The calling, inheriting or mentioning symbol's row id.</param>
/// <param name="Target">The symbol being called, inherited from or mentioned.</param>
/// <param name="Relation">One of <see cref="Extraction.RelationKinds"/>.</param>
/// <param name="Weight">How strongly the relation holds; call sites are compressed and capped.</param>
/// <param name="FileId">The file that produced the relation.</param>
internal readonly record struct IndexEdge(long Source, long Target, string Relation, int Weight, long FileId);

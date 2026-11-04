# Implementation Plan: C# compiler oracle

**Feature**: `034-csharp-compiler-oracle`
**Status**: implemented 2026-11-04

## Architecture

`SharpCoreAgent.Languages.CSharp` references Domain and
`Microsoft.CodeAnalysis.CSharp.Workspaces` 5.9.0. It owns project reading, framework/package resolution, Roslyn project
construction and the three language-neutral ports. It references no tool, DI or provider type.

`CSharpProjectProbe` maps a requested source to the nearest `.csproj` whose loaded source set contains it.
`CSharpProjectSource` reads XML and restored assets. `CSharpSolution` recursively loads in-workspace project references
and builds an `AdhocWorkspace`: each project has parsed source documents, metadata references, parse/compilation options
and project references. The target project's compilation supplies diagnostics and semantic models; Roslyn's symbol finder
searches the in-memory solution for uses.

A cached immutable snapshot is keyed by the bytes/stamps of every project, source, directory build file and assets file in
the graph. A request that changed a source rebuilds once; repeated requests reuse one solution. Snapshot creation and cache
lookup are serialized by one lock per adapter, because a tool instance is shared by the process.

## Why no MSBuildWorkspace

`MSBuildWorkspace` gives complete project evaluation by launching a design-time build host. That is a process this product
did not ask the developer to name and a second route to the machine. The constitution allows `Process.Start` only in the
shell tool and MCP client. This round therefore reads the files MSBuild would read and declines constructs it cannot model
exactly. The trade is explicit, not silent: MSBuild conditions/imports beyond the supported subset become `CSH002`.

## Framework references

The target major selects the highest installed `Microsoft.NETCore.App.Ref` pack for that major. ASP.NET Core and Windows
Desktop add their packs when the project's SDK/framework metadata asks for them. Reference-pack directories are the
compilation references, not the running process's runtime assemblies; a net8 project is therefore checked against net8
API surface even when the agent runs on .NET 11. .NET Framework and .NET Standard are refused with `CSH002` this round.

## Diagnostics, uses and signature help

Diagnostics call `GetDiagnostics` on the target compilation and filter to the requested syntax tree. Uses resolve the token
to a declared/selected symbol through the semantic model, then call `SymbolFinder.FindReferencesAsync` over the solution;
only locations with source text are returned. Signatures inspect invocation/object-creation nodes, union selected and
candidate methods, order the selected first, and map the argument containing the position to its parameter, honouring named
arguments.

## Dependency and constitution check

Domain stays BCL-only. Roslyn is an implementation dependency in a language assembly, justified because the BCL has no C#
parser or semantic model. It opens files but launches no process and makes no network call. No production tool gains a side
effect. `--no-csharp` removes registration for a host that does not want the memory/startup cost.

## Complexity and limits

The project reader is a deliberately bounded subset of MSBuild. Unsupported source globs/properties, conditional compile
items, generated documents and target frameworks without installed reference packs fail. Caching adds one snapshot object;
it does not add persisted state. Full MSBuild evaluation and source-generator support need a design that does not create an
unnamed process and belong to a later round.

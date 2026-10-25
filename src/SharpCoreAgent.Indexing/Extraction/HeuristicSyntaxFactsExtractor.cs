using System.Text.RegularExpressions;

namespace SharpCoreAgent.Indexing.Extraction;

/// <summary>
/// Reads declarations, relations and documentation out of a file with braces, indentation and regular expressions
/// instead of a compiler.
/// </summary>
/// <remarks>
/// A real parser is the right tool and it is not available here: the index reads a dozen languages without taking
/// a dependency on a dozen compilers. So this reads what is cheap and reliable — declaration headers, the base
/// list, call sites in code that is neither a comment nor a string, and the doc comment above a declaration — and
/// reports ranges a reader can open.
/// <para>
/// The bet is that approximately right is enough for retrieval: a passage is still the right passage even if the
/// declaration it holds was labelled <c>method</c> instead of <c>property</c>. What it must never do is invent a
/// relation from prose, which is why every pattern runs against <see cref="SourceMasker"/>'s output and never
/// against the raw text.
/// </para>
/// <para>
/// Everything it produces is a function of the file's bytes alone — no clock, no ambient culture, no hash-order
/// iteration — so two builds of one tree produce identical facts, which is what makes the incremental build's
/// "unchanged" answer trustworthy.
/// </para>
/// </remarks>
internal sealed partial class HeuristicSyntaxFactsExtractor : ISyntaxFactsExtractor
{
    /// <summary>The identity this extractor reports and stamps into the build.</summary>
    internal const string ExtractorId = "heuristic/1";

    /// <summary>The weight a call edge stops at, however many call sites it compresses.</summary>
    internal const int MaxCallWeight = 5;

    /// <summary>The most lines one documentation passage may hold before it is split.</summary>
    internal const int MaxDocLines = 80;

    /// <summary>
    /// The words that can precede a parenthesis without being a call: statements, operators and modifiers.
    /// </summary>
    /// <remarks>
    /// Without this list every <c>if (…)</c> becomes a call to a symbol named <c>if</c>, and a graph full of those
    /// is worse than no graph: traversal would walk into language keywords and answer with nonsense.
    /// </remarks>
    private static readonly HashSet<string> NonCallWords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "foreach", "while", "do", "switch", "case", "default", "catch", "finally", "try",
        "using", "lock", "return", "throw", "new", "await", "yield", "nameof", "typeof", "sizeof", "checked",
        "unchecked", "fixed", "stackalloc", "is", "as", "in", "out", "ref", "params", "when", "where", "select",
        "from", "let", "orderby", "group", "into", "join", "on", "equals", "by", "ascending", "descending",
        "and", "or", "not", "with", "init", "get", "set", "value", "this", "base", "var", "void", "operator",
        "delegate", "event", "add", "remove", "alignof", "decltype", "print", "assert", "defined", "catch",
    };

    /// <summary>Gets the id every result carries.</summary>
    public string Id => ExtractorId;

    /// <summary>
    /// Creates the extractor the index uses.
    /// </summary>
    /// <returns>A stateless extractor; one instance can serve every file in a build.</returns>
    public static HeuristicSyntaxFactsExtractor Create() => new();

    /// <inheritdoc />
    public SyntaxFacts Extract(ScannedFile file, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return SyntaxFacts.Empty(Id);
        }

        var lines = SourceLines.Split(text);

        return string.Equals(file.Language, "markdown", StringComparison.Ordinal)
            ? ExtractDocumentation(lines, cancellationToken)
            : ExtractCode(file, lines, SourceLines.Split(SourceMasker.Mask(text)), cancellationToken);
    }

    private static string LineAt(IReadOnlyList<string> lines, int index) =>
        index >= 0 && index < lines.Count ? lines[index] : string.Empty;

    /// <summary>
    /// Reads a Markdown file as a sequence of headed sections.
    /// </summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="cancellationToken">Cancels between sections.</param>
    /// <returns>Documentation passages only; a Markdown file declares no symbols.</returns>
    private SyntaxFacts ExtractDocumentation(List<string> lines, CancellationToken cancellationToken)
    {
        var docs = new List<ExtractedDoc>();
        string? heading = null;
        var start = 0;
        var inFence = false;

        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = lines[index];

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                // Inside a fenced block, '#' is a comment or a shell prompt, not a heading.
                inFence = !inFence;
                continue;
            }

            if (inFence || !LooksLikeHeading(line))
            {
                continue;
            }

            if (index > start)
            {
                AddDocSection(lines, start, index - 1, heading, docs);
            }

            heading = line.TrimStart().TrimStart('#').Trim();
            start = index;
        }

        if (lines.Count > start)
        {
            AddDocSection(lines, start, lines.Count - 1, heading, docs);
        }

        return new SyntaxFacts
        {
            Extractor = Id,
            Symbols = [],
            Relations = [],
            Docs = docs,
            Imports = [],
        };
    }

    private static bool LooksLikeHeading(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length > 1 && trimmed[0] == '#' && (trimmed[1] == '#' || trimmed[1] == ' ');
    }

    private static void AddDocSection(
        List<string> lines,
        int start,
        int end,
        string? heading,
        List<ExtractedDoc> docs)
    {
        while (end > start && string.IsNullOrWhiteSpace(lines[end]))
        {
            end--;
        }

        if (start > end)
        {
            return;
        }

        // A README section can run for hundreds of lines; the store wants passages, so long sections are sliced
        // and each slice repeats the heading, which is how a reader still knows where it landed.
        for (var slice = start; slice <= end; slice += MaxDocLines)
        {
            var sliceEnd = Math.Min(slice + MaxDocLines - 1, end);

            docs.Add(new ExtractedDoc
            {
                StartLine = slice + 1,
                EndLine = sliceEnd + 1,
                Heading = heading,
                Text = string.Join('\n', lines.Skip(slice).Take(sliceEnd - slice + 1)),
            });
        }
    }


    /// <summary>
    /// Reads a source file's declarations, imports and relations.
    /// </summary>
    /// <param name="file">The file being read.</param>
    /// <param name="lines">The original lines, which is where signatures and doc comments are read from.</param>
    /// <param name="code">The same lines with comments and strings blanked.</param>
    /// <param name="cancellationToken">Cancels between lines.</param>
    /// <returns>The file's facts.</returns>
    private SyntaxFacts ExtractCode(
        ScannedFile file,
        List<string> lines,
        List<string> code,
        CancellationToken cancellationToken)
    {
        var declarations = new List<Declaration>();
        var imports = new List<string>();
        var containers = new List<Declaration>();
        var indented = IsIndentationScoped(file.Language);
        var view = new FileView(code, lines, indented);
        string? declaredNamespace = null;

        for (var index = 0; index < code.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PopFinishedContainers(containers, index);

            var masked = code[index];
            if (masked.Trim().Length == 0)
            {
                continue;
            }

            var container = containers.Count > 0 ? containers[^1] : null;

            if (declaredNamespace is null && TryNamespace(masked, out var space))
            {
                declaredNamespace = space;
                continue;
            }

            if (TryImport(masked, lines[index], imports))
            {
                continue;
            }

            if (TryTypeDeclaration(masked, out var kind, out var typeName, out var bases))
            {
                var declaration = NewType(kind, typeName, container, declaredNamespace, index, bases, view);
                declarations.Add(declaration);

                if (declaration.OwnsMembers)
                {
                    containers.Add(declaration);
                }

                continue;
            }

            AddMember(declarations, masked, container, declaredNamespace, index, view);
        }

        var symbols = declarations.Select(static declaration => declaration.ToSymbol()).ToList();

        return new SyntaxFacts
        {
            Extractor = Id,
            Symbols = symbols,
            Relations = Relations(symbols, declarations, code, cancellationToken),
            Docs = [],
            Imports = imports,
        };
    }


    /// <summary>
    /// The file being read, as every declaration built from it needs to see it.
    /// </summary>
    /// <param name="Code">The masked lines, used to find each declaration's range.</param>
    /// <param name="Lines">The original lines, used for the signature and the doc comment.</param>
    /// <param name="Indented">Whether the language scopes blocks by indentation rather than braces.</param>
    private readonly record struct FileView(List<string> Code, List<string> Lines, bool Indented);

    /// <summary>
    /// Drops the open containers whose last line is behind the line being read.
    /// </summary>
    /// <param name="containers">The types that may still own declarations.</param>
    /// <param name="index">The 0-based line being read.</param>
    private static void PopFinishedContainers(List<Declaration> containers, int index)
    {
        while (containers.Count > 0 && containers[^1].End < index)
        {
            containers.RemoveAt(containers.Count - 1);
        }
    }

    /// <summary>
    /// Builds a declaration for a type, which may itself end up holding members.
    /// </summary>
    /// <param name="kind">The symbol kind.</param>
    /// <param name="name">The declared name.</param>
    /// <param name="container">The enclosing type, when there is one.</param>
    /// <param name="declaredNamespace">The namespace in force, when one was declared.</param>
    /// <param name="index">The 0-based declaration line.</param>
    /// <param name="bases">The base types named in the header.</param>
    /// <param name="view">The file being read.</param>
    /// <returns>The declaration.</returns>
    private static Declaration NewType(
        string kind,
        string name,
        Declaration? container,
        string? declaredNamespace,
        int index,
        IReadOnlyList<string> bases,
        FileView view) => new()
    {
        Kind = kind,
        Name = name,
        Container = container?.QualifiedName,
        Namespace = declaredNamespace,
        Start = index,
        End = ComputeRange(view.Code, index, view.Indented),
        Signature = ReadSignature(view.Lines, index),
        Bases = bases,
        RawDoc = DocComment(view.Lines, index),
    };

    /// <summary>
    /// Adds the member a line declares, when it declares one.
    /// </summary>
    /// <param name="declarations">Where the declaration is added.</param>
    /// <param name="masked">The line, with comments and strings blanked.</param>
    /// <param name="container">The enclosing type, when there is one.</param>
    /// <param name="declaredNamespace">The namespace in force, when one was declared.</param>
    /// <param name="index">The 0-based line being read.</param>
    /// <param name="view">The file being read.</param>
    private static void AddMember(
        List<Declaration> declarations,
        string masked,
        Declaration? container,
        string? declaredNamespace,
        int index,
        FileView view)
    {
        if (TryCallable(masked, container?.Name, out var callableName))
        {
            declarations.Add(NewMember(
                SymbolKinds.Method,
                callableName,
                container,
                declaredNamespace,
                index,
                ComputeRange(view.Code, index, view.Indented),
                view.Lines));
            return;
        }

        if (container is null)
        {
            return;
        }

        if (TryPropertyDeclaration(masked, out var propertyName))
        {
            declarations.Add(NewMember(
                SymbolKinds.Property,
                propertyName,
                container,
                declaredNamespace,
                index,
                ComputeRange(view.Code, index, view.Indented),
                view.Lines));
            return;
        }

        if (TryFieldDeclaration(masked, out var fieldName))
        {
            declarations.Add(NewMember(
                SymbolKinds.Field,
                fieldName,
                container,
                declaredNamespace,
                index,
                index,
                view.Lines));
        }
    }

    /// <summary>
    /// Builds a declaration for one member of an open type.
    /// </summary>
    /// <param name="kind">The symbol kind.</param>
    /// <param name="name">The declared name.</param>
    /// <param name="container">The enclosing type, which a top-level function does not have.</param>
    /// <param name="declaredNamespace">The namespace in force, when one was declared.</param>
    /// <param name="start">The 0-based first line.</param>
    /// <param name="end">The 0-based last line, inclusive.</param>
    /// <param name="lines">The original lines, used for the signature and doc comment.</param>
    /// <returns>The declaration.</returns>
    private static Declaration NewMember(
        string kind,
        string name,
        Declaration? container,
        string? declaredNamespace,
        int start,
        int end,
        List<string> lines) => new()
    {
        Kind = kind,
        Name = name,
        Container = container?.QualifiedName,
        Namespace = declaredNamespace,
        Start = start,
        End = end,
        Signature = ReadSignature(lines, start),
        RawDoc = DocComment(lines, start),
    };

    /// <summary>
    /// One declaration as it was found: the parts that are interesting before names become keys.
    /// </summary>
    private sealed record Declaration
    {
        /// <summary>Gets the symbol kind.</summary>
        public required string Kind { get; init; }

        /// <summary>Gets the declared name.</summary>
        public required string Name { get; init; }

        /// <summary>Gets the enclosing type's qualified name, or <see langword="null"/>.</summary>
        public string? Container { get; init; }

        /// <summary>Gets the namespace, or <see langword="null"/>.</summary>
        public string? Namespace { get; init; }

        /// <summary>Gets the 0-based first line.</summary>
        public required int Start { get; init; }

        /// <summary>Gets the 0-based last line, inclusive.</summary>
        public required int End { get; init; }

        /// <summary>Gets the declaration line, trimmed.</summary>
        public string? Signature { get; init; }

        /// <summary>Gets the doc comment above the declaration, tags and all.</summary>
        public string? RawDoc { get; init; }

        /// <summary>Gets the base types named in the header, in written order.</summary>
        public IReadOnlyList<string> Bases { get; init; } = [];

        /// <summary>Gets the dotted name without the kind suffix.</summary>
        public string QualifiedName => (Container ?? Namespace) is { } prefix ? prefix + "." + Name : Name;

        /// <summary>Gets the stable key.</summary>
        public string Key => QualifiedName + "#" + Kind;

        /// <summary>Gets a value indicating whether this declaration is a type other declarations live in.</summary>
        public bool OwnsMembers => Kind is SymbolKinds.Class or SymbolKinds.Interface or SymbolKinds.Record
            or SymbolKinds.Struct or SymbolKinds.Enum;

        /// <summary>
        /// Projects the declaration onto the record the writer stores.
        /// </summary>
        /// <returns>The symbol, with 1-based inclusive lines.</returns>
        public ExtractedSymbol ToSymbol() => new()
        {
            Key = Key,
            Name = Name,
            QualifiedName = QualifiedName,
            Kind = Kind,
            StartLine = Start + 1,
            EndLine = End + 1,
            Namespace = Namespace,
            Container = Container,
            Signature = Signature,
            Doc = CleanDoc(RawDoc),
        };
    }

    private static bool IsIndentationScoped(string language) =>
        language is "python" or "ruby";

    /// <summary>
    /// Finds the last line of a declaration.
    /// </summary>
    /// <param name="code">The masked lines.</param>
    /// <param name="start">The declaration's first line.</param>
    /// <param name="indented">Whether the language scopes blocks by indentation rather than braces.</param>
    /// <returns>The last line, inclusive, or <paramref name="start"/> when the declaration is a single line.</returns>
    private static int ComputeRange(IReadOnlyList<string> code, int start, bool indented) =>
        indented ? IndentedRange(code, start) : BracedRange(code, start);

    private static int BracedRange(IReadOnlyList<string> code, int start)
    {
        var depth = 0;
        var opened = false;

        for (var index = start; index < code.Count; index++)
        {
            if (ClosesOn(code[index], ref depth, ref opened))
            {
                return index;
            }

            if (!opened && code[index].Contains(';', StringComparison.Ordinal))
            {
                // A bodyless declaration ("public abstract void Run();") ends at its semicolon, which may be on a
                // later line than the header.
                return index;
            }
        }

        // An unbalanced file: the declaration does run to the end, and saying so beats inventing a range.
        return opened ? Math.Max(start, code.Count - 1) : start;
    }

    /// <summary>
    /// Counts the braces on one line and reports whether the block it opened also closed on it.
    /// </summary>
    /// <param name="line">The masked line.</param>
    /// <param name="depth">The brace depth, carried across lines.</param>
    /// <param name="opened">Whether a block has been opened, carried across lines.</param>
    /// <returns><see langword="true"/> when the line closes the block the declaration opened.</returns>
    private static bool ClosesOn(string line, ref int depth, ref bool opened)
    {
        foreach (var character in line)
        {
            if (character == '{')
            {
                depth++;
                opened = true;
            }
            else if (character == '}')
            {
                depth--;

                if (opened && depth <= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int IndentedRange(IReadOnlyList<string> code, int start)
    {
        var startIndent = Indentation(code[start]);
        var end = start;

        for (var index = start + 1; index < code.Count; index++)
        {
            var line = code[index];

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (Indentation(line) <= startIndent)
            {
                break;
            }

            end = index;
        }

        return end;
    }

    private static int Indentation(string line)
    {
        var indent = 0;

        foreach (var character in line)
        {
            if (character == ' ')
            {
                indent++;
            }
            else if (character == '\t')
            {
                indent += 4;
            }
            else
            {
                break;
            }
        }

        return indent;
    }


    private static bool TryNamespace(string masked, out string name)
    {
        var match = NamespacePattern().Match(masked);

        name = match.Success ? match.Groups["name"].Value : string.Empty;

        return match.Success;
    }

    /// <summary>
    /// Recognises an import and appends its target to <paramref name="imports"/>.
    /// </summary>
    /// <param name="masked">The line with comments and strings blanked.</param>
    /// <param name="raw">The original line, needed for ECMAScript's quoted module paths.</param>
    /// <param name="imports">The list to append to; duplicates are the caller's problem to remove.</param>
    /// <returns><see langword="true"/> when the line was an import.</returns>
    private static bool TryImport(string masked, string raw, List<string> imports)
    {
        var usingMatch = UsingPattern().Match(masked);
        if (usingMatch.Success)
        {
            imports.Add(usingMatch.Groups["name"].Value);
            return true;
        }

        var pythonMatch = PythonImportPattern().Match(masked);
        if (pythonMatch.Success)
        {
            if (pythonMatch.Groups["from"].Success)
            {
                imports.Add(pythonMatch.Groups["from"].Value);
            }
            else
            {
                foreach (var module in pythonMatch.Groups["names"].Value.Split(','))
                {
                    var name = module.Trim();
                    if (name.Length > 0)
                    {
                        imports.Add(name);
                    }
                }
            }

            return true;
        }

        var ecmaMatch = EcmaImportPattern().Match(raw);
        if (ecmaMatch.Success)
        {
            imports.Add(ecmaMatch.Groups["name"].Value);
            return true;
        }

        return false;
    }

    private static bool TryTypeDeclaration(
        string masked,
        out string kind,
        out string name,
        out IReadOnlyList<string> bases)
    {
        kind = string.Empty;
        name = string.Empty;
        bases = [];

        // Python writes its bases in parentheses ("class Circle(Shape):"), which the brace-language pattern would
        // read as a class with no base list, so it is tried first.
        var pythonMatch = PythonClassPattern().Match(masked);
        if (pythonMatch.Success)
        {
            kind = SymbolKinds.Class;
            name = pythonMatch.Groups["name"].Value;
            bases = BaseNames(pythonMatch.Groups["bases"].Value);

            return true;
        }

        var match = TypePattern().Match(masked);
        if (!match.Success)
        {
            return false;
        }

        kind = match.Groups["keyword"].Value switch
        {
            "interface" or "trait" => SymbolKinds.Interface,
            "struct" or "record struct" => SymbolKinds.Struct,
            "enum" => SymbolKinds.Enum,
            "record" or "record class" => SymbolKinds.Record,
            _ => SymbolKinds.Class,
        };

        name = match.Groups["name"].Value;
        bases = BaseNames(match.Groups["bases"].Value);

        return true;
    }

    /// <summary>
    /// Reduces a base list to simple type names.
    /// </summary>
    /// <param name="value">The text after the colon or inside the parentheses.</param>
    /// <returns>The names, in written order, without generic arguments or generic constraints.</returns>
    private static List<string> BaseNames(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var names = new List<string>();

        foreach (var part in value.Split(','))
        {
            var candidate = part.Trim();

            var modifier = candidate.IndexOf(" where ", StringComparison.Ordinal);
            if (modifier >= 0)
            {
                candidate = candidate[..modifier];
            }

            var generic = candidate.IndexOf('<', StringComparison.Ordinal);
            if (generic >= 0)
            {
                candidate = candidate[..generic];
            }

            candidate = candidate.Trim().TrimEnd('{', ';', ':').Trim();

            var dot = candidate.LastIndexOf('.');
            if (dot >= 0)
            {
                candidate = candidate[(dot + 1)..];
            }

            if (candidate.Length > 0 && !NonCallWords.Contains(candidate))
            {
                names.Add(candidate);
            }
        }

        return names;
    }


    /// <summary>
    /// Recognises a callable declaration: a C#-style signature, or a language whose keyword does the work.
    /// </summary>
    /// <param name="masked">The line with comments and strings blanked.</param>
    /// <param name="containerName">The enclosing type's simple name, so a constructor is recognised.</param>
    /// <param name="name">Receives the declared name when the line is a declaration.</param>
    /// <returns><see langword="true"/> when the line declares a callable.</returns>
    private static bool TryCallable(string masked, string? containerName, out string name)
    {
        name = string.Empty;

        if (StartsLikeStatement(masked))
        {
            return false;
        }

        var keywordMatch = CallablePattern().Match(masked);
        if (keywordMatch.Success)
        {
            name = keywordMatch.Groups["name"].Value;
            return !NonCallWords.Contains(name);
        }

        var match = MethodPattern().Match(masked);
        if (!match.Success)
        {
            return false;
        }

        var candidate = match.Groups["name"].Value;

        if (NonCallWords.Contains(candidate))
        {
            return false;
        }

        var hasReturnType = match.Groups["type"].Success;
        var hasModifier = match.Groups["mods"].Value.Trim().Length > 0;

        // "Run();" at the start of a line is a call, not a declaration; "Circle(double r)" inside Circle is a
        // constructor, which is the one case where a declaration has neither a return type nor a modifier.
        if (!hasReturnType && !hasModifier)
        {
            return string.Equals(candidate, containerName, StringComparison.Ordinal);
        }

        name = candidate;

        return true;
    }

    private static bool StartsLikeStatement(string masked)
    {
        var trimmed = masked.AsSpan().TrimStart();

        // Attributes are not statements, and they are by far the most common thing to meet first.
        while (trimmed.Length > 0 && trimmed[0] == '[')
        {
            var close = trimmed.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            trimmed = trimmed[(close + 1)..].TrimStart();
        }

        var end = 0;
        while (end < trimmed.Length && (char.IsLetterOrDigit(trimmed[end]) || trimmed[end] == '_'))
        {
            end++;
        }

        return end > 0 && NonCallWords.Contains(trimmed[..end].ToString());
    }

    private static bool TryPropertyDeclaration(string masked, out string name)
    {
        var match = PropertyPattern().Match(masked);

        name = match.Success ? match.Groups["name"].Value : string.Empty;

        return match.Success && !NonCallWords.Contains(name);
    }

    private static bool TryFieldDeclaration(string masked, out string name)
    {
        name = string.Empty;

        var match = FieldPattern().Match(masked);
        if (!match.Success)
        {
            return false;
        }

        // A local variable has no accessibility: "int count = 0;" is a statement, "private int _count;" a field.
        if (!FieldModifierPattern().IsMatch(match.Groups["mods"].Value))
        {
            return false;
        }

        name = match.Groups["name"].Value;

        return !NonCallWords.Contains(name);
    }

    private static string? ReadSignature(IReadOnlyList<string> lines, int index)
    {
        var line = LineAt(lines, index).Trim();

        return line.Length == 0 ? null : line;
    }

    /// <summary>
    /// Reads the doc comment immediately above a declaration.
    /// </summary>
    /// <param name="lines">The original lines.</param>
    /// <param name="index">The declaration's index.</param>
    /// <returns>The comment's text with its tags intact, or <see langword="null"/> when there is no comment.</returns>
    private static string? DocComment(IReadOnlyList<string> lines, int index)
    {
        var collected = new List<string>();
        var cursor = index - 1;

        while (cursor >= 0)
        {
            var line = LineAt(lines, cursor).Trim();

            if (!line.StartsWith("///", StringComparison.Ordinal))
            {
                break;
            }

            collected.Add(line[3..]);
            cursor--;
        }

        if (collected.Count == 0)
        {
            return null;
        }

        collected.Reverse();

        var text = string.Join(' ', collected).Trim();

        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Turns a doc comment into the summary the index stores.
    /// </summary>
    /// <param name="raw">The comment, tags and all.</param>
    /// <returns>The summary text, or <see langword="null"/> when the comment says nothing.</returns>
    private static string? CleanDoc(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var text = raw;
        var summary = SummaryPattern().Match(text);

        if (summary.Success)
        {
            text = summary.Groups["text"].Value;
        }

        text = XmlTagPattern().Replace(text, " ");
        text = WhitespacePattern().Replace(text, " ").Trim();

        return text.Length == 0 ? null : text;
    }


    /// <summary>
    /// Turns declarations and call sites into the relations the writer stores.
    /// </summary>
    /// <param name="symbols">The stored symbols, in file order.</param>
    /// <param name="declarations">The same declarations, before they became symbols.</param>
    /// <param name="code">The masked lines, which is where call sites are read.</param>
    /// <param name="cancellationToken">Cancels between lines.</param>
    /// <returns>The relations, ordered so that two builds emit the same list.</returns>
    private static List<SyntaxRelation> Relations(
        IReadOnlyList<ExtractedSymbol> symbols,
        IReadOnlyList<Declaration> declarations,
        List<string> code,
        CancellationToken cancellationToken)
    {
        var relations = new List<SyntaxRelation>();

        AddBaseRelations(declarations, relations);

        var calls = new Dictionary<(string Source, string Target), int>();
        var owners = Owners(symbols, code.Count);

        CollectCalls(owners, code, calls, cancellationToken);
        AddCallRelations(calls, relations);
        AddDocMentionRelations(declarations, relations);

        return relations
            .OrderBy(static relation => relation.Source ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static relation => relation.Kind, StringComparer.Ordinal)
            .ThenBy(static relation => relation.Target, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Counts how often each declaration calls which name, attributing a line to the declaration that owns it.
    /// </summary>
    /// <param name="owners">For each line, the symbol that owns it.</param>
    /// <param name="code">The masked lines, where call sites are visible.</param>
    /// <param name="calls">The counts, keyed by source key and called name.</param>
    /// <param name="cancellationToken">Cancels between lines.</param>
    private static void CollectCalls(
        ExtractedSymbol?[] owners,
        List<string> code,
        Dictionary<(string Source, string Target), int> calls,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < code.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var owner = owners[index];
            var masked = code[index];

            if (owner is null || masked.Trim().Length == 0)
            {
                continue;
            }

            foreach (Match match in CallPattern().Matches(masked))
            {
                if (CallTarget(match, index, owner) is { } target)
                {
                    var key = (owner.Key, target);
                    calls[key] = calls.TryGetValue(key, out var count) ? count + 1 : 1;
                }
            }
        }
    }

    /// <summary>
    /// Answers which name a call site calls, or <see langword="null"/> when it calls nothing worth an edge.
    /// </summary>
    /// <param name="match">The call-shaped match found on the line.</param>
    /// <param name="index">The 0-based line the match was found on.</param>
    /// <param name="owner">The declaration that owns the line.</param>
    /// <returns>The called name, or <see langword="null"/> for a keyword or for the declaration itself.</returns>
    private static string? CallTarget(Match match, int index, ExtractedSymbol owner)
    {
        var target = match.Groups["name"].Value;

        if (NonCallWords.Contains(target))
        {
            return null;
        }

        // The declaration's own name on its own line is a signature, not a call to itself.
        return index == owner.StartLine - 1 && string.Equals(target, owner.Name, StringComparison.Ordinal)
            ? null
            : target;
    }

    /// <summary>
    /// Writes one edge per called name, weighted by how often it was called.
    /// </summary>
    /// <param name="calls">The counts, keyed by source key and called name.</param>
    /// <param name="relations">Where the edges are added.</param>
    private static void AddCallRelations(
        Dictionary<(string Source, string Target), int> calls,
        List<SyntaxRelation> relations)
    {
        foreach (var ((source, target), count) in calls)
        {
            relations.Add(new SyntaxRelation
            {
                Source = source,
                Target = target,
                Kind = RelationKinds.Calls,
                Weight = Math.Min(count, MaxCallWeight),
            });
        }
    }

    private static void AddDocMentionRelations(
        IReadOnlyList<Declaration> declarations,
        List<SyntaxRelation> relations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration.RawDoc is null)
            {
                continue;
            }

            foreach (Match match in CrefPattern().Matches(declaration.RawDoc))
            {
                var target = SimpleName(match.Groups["name"].Value);

                if (target.Length > 0 && !string.Equals(target, declaration.Name, StringComparison.Ordinal))
                {
                    relations.Add(new SyntaxRelation
                    {
                        Source = declaration.Key,
                        Target = target,
                        Kind = RelationKinds.DocMentions,
                    });
                }
            }
        }
    }


    private static void AddBaseRelations(IReadOnlyList<Declaration> declarations, List<SyntaxRelation> relations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration.Kind == SymbolKinds.Enum)
            {
                // "enum Colour : byte" names an underlying type, not a base type; an edge there would be a lie.
                continue;
            }

            for (var index = 0; index < declaration.Bases.Count; index++)
            {
                var target = declaration.Bases[index];

                // In C# the first base of a class is its base class and the rest are interfaces; a struct's and an
                // interface's bases are interfaces either way. "IFoo" is the only interface convention worth
                // betting on, and getting it wrong costs one edge's label, not the edge.
                var inherits = declaration.Kind is SymbolKinds.Interface
                    || (declaration.Kind is SymbolKinds.Class or SymbolKinds.Record
                        && index == 0
                        && !LooksLikeInterfaceName(target));

                relations.Add(new SyntaxRelation
                {
                    Source = declaration.Key,
                    Target = target,
                    Kind = inherits ? RelationKinds.Inherits : RelationKinds.Implements,
                });
            }
        }
    }

    private static bool LooksLikeInterfaceName(string name) =>
        name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]);

    /// <summary>
    /// Works out, for every line, which declaration owns it: the innermost member, or the type around it.
    /// </summary>
    /// <param name="symbols">The symbols, in file order.</param>
    /// <param name="lineCount">How many lines the file has.</param>
    /// <returns>One owner per line, or <see langword="null"/> where nothing encloses the line.</returns>
    private static ExtractedSymbol?[] Owners(IReadOnlyList<ExtractedSymbol> symbols, int lineCount)
    {
        var owners = new ExtractedSymbol?[lineCount];
        var active = new List<ExtractedSymbol>();
        var next = 0;

        for (var index = 0; index < lineCount; index++)
        {
            while (next < symbols.Count && symbols[next].StartLine - 1 <= index)
            {
                active.Add(symbols[next]);
                next++;
            }

            active.RemoveAll(symbol => symbol.EndLine - 1 < index);

            owners[index] = active.FindLast(static symbol => !symbol.IsContainer) ?? active.LastOrDefault();
        }

        return owners;
    }

    private static string SimpleName(string name)
    {
        var trimmed = name.Trim();

        var generic = trimmed.IndexOf('<', StringComparison.Ordinal);
        if (generic >= 0)
        {
            trimmed = trimmed[..generic];
        }

        var dot = trimmed.LastIndexOf('.');
        if (dot >= 0)
        {
            trimmed = trimmed[(dot + 1)..];
        }

        return trimmed.Trim();
    }

    [GeneratedRegex(@"^\s*namespace\s+(?<name>[A-Za-z_][\w.]*)", RegexOptions.CultureInvariant)]
    private static partial Regex NamespacePattern();

    [GeneratedRegex(
        @"^\s*(?:global\s+)?using\s+(?:(?:static|unsafe)\s+)*(?:(?<alias>[A-Za-z_]\w*)\s*=\s*)?(?<name>[A-Za-z_][\w.]*)\s*;",
        RegexOptions.CultureInvariant)]
    private static partial Regex UsingPattern();

    [GeneratedRegex(
        @"^\s*(?:from\s+(?<from>[A-Za-z_][\w.]*)\s+import\s+[\w\s,*()]*|import\s+(?<names>[A-Za-z_][\w.]*(?:\s*,\s*[A-Za-z_][\w.]*)*))",
        RegexOptions.CultureInvariant)]
    private static partial Regex PythonImportPattern();

    [GeneratedRegex(
        @"^\s*import\s+[^;]*?\bfrom\s+['""](?<name>[^'""]+)['""]",
        RegexOptions.CultureInvariant)]
    private static partial Regex EcmaImportPattern();

    [GeneratedRegex(
        @"^\s*(?:\[[^\]]*\]\s*)*(?<mods>(?:(?:public|internal|private|protected|static|sealed|abstract|partial|file|unsafe|new|readonly|ref|export|default|open|final|data|pub)\s+)*)(?<keyword>record\s+struct|record\s+class|class|interface|record|struct|enum|trait)\s+(?<name>[A-Za-z_]\w*)\s*(?:<[^<>{};]*>)?\s*(?::\s*(?<bases>[^{;]*))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex TypePattern();

    [GeneratedRegex(
        @"^\s*class\s+(?<name>[A-Za-z_]\w*)\s*(?:\((?<bases>[^)]*)\))?\s*:",
        RegexOptions.CultureInvariant)]
    private static partial Regex PythonClassPattern();


    [GeneratedRegex(
        @"^\s*(?:export\s+|public\s+|private\s+|internal\s+|static\s+|async\s+|const\s+|suspend\s+|inline\s+|extern\s+|unsafe\s+|override\s+|virtual\s+|open\s+|pub(?:\([^)]*\))?\s+)*(?:def|func|fn|fun|function|sub|proc)\s+(?<name>[A-Za-z_]\w*)\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex CallablePattern();

    [GeneratedRegex(
        @"^\s*(?:\[[^\]]*\]\s*)*(?<mods>(?:(?:public|internal|private|protected|static|virtual|override|abstract|sealed|async|partial|extern|unsafe|new|readonly|required|file|export|default|synchronized|final)\s+)*)(?:(?<type>[A-Za-z_][\w.<>\[\],?]*(?:\s*<[^<>{};]*>)?)\s+)?(?<name>[A-Za-z_]\w*)\s*(?:<[^<>{};]*>)?\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex MethodPattern();

    [GeneratedRegex(
        @"^\s*(?:\[[^\]]*\]\s*)*(?<mods>(?:(?:public|internal|private|protected|static|virtual|override|abstract|sealed|new|required|readonly|unsafe|export)\s+)*)(?<type>[A-Za-z_][\w.<>\[\],?]*(?:\s*<[^<>{};]*>)?)\s+(?<name>[A-Za-z_]\w*)\s*\{\s*(?:get|set|init)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex PropertyPattern();

    [GeneratedRegex(
        @"^\s*(?:\[[^\]]*\]\s*)*(?<mods>(?:(?:public|internal|private|protected|static|readonly|const|volatile|new|unsafe|event)\s+)*)(?<type>[A-Za-z_][\w.]*(?:\s*<[^<>{};]*>)?(?:\[\s*\])*\??)\s+(?<name>[A-Za-z_]\w*)\s*(?:=[^;]*)?;",
        RegexOptions.CultureInvariant)]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"\b(?:public|internal|private|protected|static|readonly|const|volatile)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex FieldModifierPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?<name>[A-Za-z_]\w*)\s*(?:<[^<>()]*>)?\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex CallPattern();

    [GeneratedRegex(@"cref\s*=\s*[""'](?<name>[^""']+)[""']", RegexOptions.CultureInvariant)]
    private static partial Regex CrefPattern();

    [GeneratedRegex(@"<summary>(?<text>[\s\S]*?)</summary>", RegexOptions.CultureInvariant)]
    private static partial Regex SummaryPattern();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex XmlTagPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

}

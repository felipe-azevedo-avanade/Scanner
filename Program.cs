using System.Collections.Concurrent;
using System.Configuration;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

// ══════════════════════════════════════════════════════════════
//  ENTRY POINT
// ══════════════════════════════════════════════════════════════

class Program
{
    static async Task Main()
    {
        using var loggerFactory = LoggerFactory.Create(b =>
            b.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            })
            .SetMinimumLevel(LogLevel.Debug));

        var logger = loggerFactory.CreateLogger<Program>();

        try
        {
            // ── 1. Carregar configurações ──────────────────────────────
            var projectPath = ConfigurationManager.AppSettings["ProjectPath"]
                ?? throw new InvalidOperationException("App.config não contém 'ProjectPath'.");

            var outputPath = ConfigurationManager.AppSettings["LogOutputPath"]
                ?? throw new InvalidOperationException("App.config não contém 'LogOutputPath'.");

            var ignoredDirs = (ConfigurationManager.AppSettings["IgnoredDirectories"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var maxParallel = int.TryParse(
                ConfigurationManager.AppSettings["MaxDegreeOfParallelism"], out var mdp) ? mdp : 4;

            if (!Directory.Exists(projectPath))
                throw new DirectoryNotFoundException($"Projeto não encontrado: {projectPath}");

            Directory.CreateDirectory(outputPath);

            logger.LogInformation("ProjectPath : {Path}", projectPath);
            logger.LogInformation("OutputPath  : {Path}", outputPath);
            logger.LogInformation("IgnoredDirs : {Dirs}", string.Join(", ", ignoredDirs));
            logger.LogInformation("Parallelism : {N}", maxParallel);

            // ── 2. Listar arquivos ────────────────────────────────────
            var csFiles = Directory
                .GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
                .Where(f => !ignoredDirs.Any(d =>
                    f.Contains(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase)))
                .ToList();

            logger.LogInformation("Arquivos .cs encontrados: {Count}", csFiles.Count);

            // ── 3. Parsear arquivos em paralelo ───────────────────────
            var bag = new ConcurrentBag<FileOracle>();
            var processed = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = maxParallel };

            await Parallel.ForEachAsync(csFiles, options, async (filePath, _) =>
            {
                var fileOracle = await ParseFileAsync(filePath);
                if (fileOracle is not null)
                    bag.Add(fileOracle);

                var count = Interlocked.Increment(ref processed);
                if (count % 10 == 0 || count == csFiles.Count)
                    logger.LogDebug("Progresso: {Done}/{Total}", count, csFiles.Count);
            });

            var files = bag.OrderBy(f => f.FileName).ToList();

            // ── 4. Montar modelo do projeto ───────────────────────────
            var project = new ProjectOracle
            {
                RootPath = projectPath,
                GeneratedAt = DateTime.Now,
                TotalFiles = files.Count,
                TotalTypes = files.Sum(f => f.Types.Count),
                TotalMethods = files.Sum(f => f.Types.Sum(t => t.Methods.Count)),
                TotalProperties = files.Sum(f => f.Types.Sum(t => t.Properties.Count)),
                Files = files
            };

            // ── 5. Gerar relatórios em paralelo ───────────────────────
            await Task.WhenAll(
                GenerateJsonAsync(project, outputPath, logger),
                GenerateMarkdownAsync(project, outputPath, logger),
                GenerateLlmContextAsync(project, outputPath, logger)
            );

            logger.LogInformation("Oracle Pack gerado com sucesso.");
            logger.LogInformation("Arquivos salvos em: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Falha fatal durante a execução.");
        }

        Console.WriteLine("\nPressione ENTER para sair...");
        Console.ReadLine();
    }

    // ══════════════════════════════════════════════════════════════
    //  PARSING — Roslyn
    // ══════════════════════════════════════════════════════════════

    static async Task<FileOracle?> ParseFileAsync(string filePath)
    {
        var code = await File.ReadAllTextAsync(filePath);
        var tree = CSharpSyntaxTree.ParseText(code);

        if (await tree.GetRootAsync() is not CompilationUnitSyntax root)
            return null;

        var fileOracle = new FileOracle
        {
            FileName = Path.GetFileName(filePath),
            RelativePath = filePath,
            Usings = root.Usings.Select(u => u.Name?.ToString() ?? "").ToList(),
            Types = []
        };

        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            fileOracle.Types.Add(ParseType(typeDecl));

        return fileOracle.Types.Count > 0 ? fileOracle : null;
    }

    static TypeOracle ParseType(TypeDeclarationSyntax typeDecl)
    {
        return new TypeOracle
        {
            Name = typeDecl.Identifier.Text,
            Kind = typeDecl.Keyword.Text,
            Namespace = GetNamespace(typeDecl),
            Modifiers = typeDecl.Modifiers.ToString(),
            XmlSummary = GetXmlSummary(typeDecl),
            BaseTypes = typeDecl.BaseList?.Types.Select(t => t.ToString()).ToList() ?? [],
            Attributes = GetAttributes(typeDecl.AttributeLists),
            Constructors = typeDecl.Members.OfType<ConstructorDeclarationSyntax>().Select(ParseConstructor).ToList(),
            Properties = typeDecl.Members.OfType<PropertyDeclarationSyntax>().Select(ParseProperty).ToList(),
            Fields = typeDecl.Members.OfType<FieldDeclarationSyntax>().Select(ParseField).ToList(),
            Methods = typeDecl.Members.OfType<MethodDeclarationSyntax>().Select(ParseMethod).ToList()
        };
    }

    static MethodOracle ParseMethod(MethodDeclarationSyntax method)
    {
        return new MethodOracle
        {
            Name = method.Identifier.Text,
            ReturnType = method.ReturnType.ToString(),
            Modifiers = method.Modifiers.ToString(),
            IsAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)),
            LinesOfCode = CountLines(method),
            XmlSummary = GetXmlSummary(method),
            Attributes = GetAttributes(method.AttributeLists),
            Parameters = method.ParameterList.Parameters.Select(p => new ParameterOracle
            {
                Name = p.Identifier.Text,
                Type = p.Type?.ToString() ?? "?",
                Default = p.Default?.Value.ToString()
            }).ToList(),
            Calls = method.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(i => i.Expression.ToString())
                .Distinct()
                .OrderBy(x => x)
                .ToList()
        };
    }

    static PropertyOracle ParseProperty(PropertyDeclarationSyntax prop)
    {
        return new PropertyOracle
        {
            Name = prop.Identifier.Text,
            Type = prop.Type.ToString(),
            Modifiers = prop.Modifiers.ToString(),
            HasGetter = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? false,
            HasSetter = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) ?? false,
            XmlSummary = GetXmlSummary(prop),
            Attributes = GetAttributes(prop.AttributeLists)
        };
    }

    static FieldOracle ParseField(FieldDeclarationSyntax field)
    {
        return new FieldOracle
        {
            Names = field.Declaration.Variables.Select(v => v.Identifier.Text).ToList(),
            Type = field.Declaration.Type.ToString(),
            Modifiers = field.Modifiers.ToString()
        };
    }

    static ConstructorOracle ParseConstructor(ConstructorDeclarationSyntax ctor)
    {
        return new ConstructorOracle
        {
            Modifiers = ctor.Modifiers.ToString(),
            XmlSummary = GetXmlSummary(ctor),
            Parameters = ctor.ParameterList.Parameters.Select(p => new ParameterOracle
            {
                Name = p.Identifier.Text,
                Type = p.Type?.ToString() ?? "?",
                Default = p.Default?.Value.ToString()
            }).ToList()
        };
    }

    // ── Helpers Roslyn ────────────────────────────────────────────

    static string GetNamespace(SyntaxNode node)
        => node.Ancestors()
               .OfType<BaseNamespaceDeclarationSyntax>()
               .FirstOrDefault()
               ?.Name.ToString() ?? "<global>";

    static string? GetXmlSummary(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia()
            .Select(t => t.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .FirstOrDefault();

        if (trivia is null) return null;

        var summary = trivia.ChildNodes()
            .OfType<XmlElementSyntax>()
            .FirstOrDefault(x => x.StartTag.Name.ToString() == "summary");

        if (summary is null) return null;

        return string.Concat(
                summary.Content.OfType<XmlTextSyntax>()
                       .SelectMany(t => t.TextTokens)
                       .Select(t => t.ToString()))
               .Trim()
               .Replace("\r\n", " ")
               .Replace("\n", " ");
    }

    static List<string> GetAttributes(SyntaxList<AttributeListSyntax> lists)
        => lists.SelectMany(al => al.Attributes)
                .Select(a => a.ToString())
                .ToList();

    static int CountLines(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
    }

    // ══════════════════════════════════════════════════════════════
    //  GERAÇÃO DE RELATÓRIOS
    // ══════════════════════════════════════════════════════════════

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    static async Task GenerateJsonAsync(ProjectOracle project, string outputPath, ILogger logger)
    {
        var json = JsonSerializer.Serialize(project, JsonOpts);
        var path = Path.Combine(outputPath, "oracle.json");
        await File.WriteAllTextAsync(path, json);
        logger.LogInformation("✔ oracle.json criado ({Size} KB)", json.Length / 1024);
    }

    static async Task GenerateMarkdownAsync(ProjectOracle project, string outputPath, ILogger logger)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Oracle Pack");
        sb.AppendLine();
        sb.AppendLine($"> Gerado em: {project.GeneratedAt:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine($"> Root: `{project.RootPath}`");
        sb.AppendLine();
        sb.AppendLine("## 📊 Estatísticas");
        sb.AppendLine("| Métrica | Valor |");
        sb.AppendLine("|---------|-------|");
        sb.AppendLine($"| Arquivos    | {project.TotalFiles}      |");
        sb.AppendLine($"| Tipos       | {project.TotalTypes}      |");
        sb.AppendLine($"| Métodos     | {project.TotalMethods}    |");
        sb.AppendLine($"| Propriedades| {project.TotalProperties} |");
        sb.AppendLine();

        foreach (var file in project.Files)
        {
            sb.AppendLine($"## 📄 {file.FileName}");
            if (file.Usings.Count > 0)
                sb.AppendLine($"*Usings: {string.Join(", ", file.Usings)}*");
            sb.AppendLine();

            foreach (var type in file.Types)
            {
                sb.AppendLine($"### {type.Kind} `{type.Namespace}.{type.Name}`");

                if (!string.IsNullOrWhiteSpace(type.XmlSummary))
                    sb.AppendLine($"> {type.XmlSummary}");

                sb.AppendLine($"- **Modificadores:** `{type.Modifiers}`");

                if (type.BaseTypes.Count > 0)
                    sb.AppendLine($"- **Herda/Implementa:** {string.Join(", ", type.BaseTypes.Select(b => $"`{b}`"))}");

                if (type.Attributes.Count > 0)
                    sb.AppendLine($"- **Atributos:** {string.Join(", ", type.Attributes.Select(a => $"`[{a}]`"))}");

                if (type.Constructors.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("#### 🔨 Construtores");
                    foreach (var ctor in type.Constructors)
                    {
                        var parms = string.Join(", ", ctor.Parameters.Select(p => $"{p.Type} {p.Name}"));
                        sb.AppendLine($"- `{ctor.Modifiers} {type.Name}({parms})`");
                        if (!string.IsNullOrWhiteSpace(ctor.XmlSummary))
                            sb.AppendLine($"  > {ctor.XmlSummary}");
                    }
                }

                if (type.Properties.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("#### 📌 Propriedades");
                    foreach (var prop in type.Properties)
                    {
                        var accessors = string.Join("; ", new[]
                            { prop.HasGetter ? "get" : null, prop.HasSetter ? "set" : null }
                            .Where(x => x != null));
                        sb.AppendLine($"- `{prop.Modifiers} {prop.Type} {prop.Name}` {{ {accessors} }}");
                        if (!string.IsNullOrWhiteSpace(prop.XmlSummary))
                            sb.AppendLine($"  > {prop.XmlSummary}");
                    }
                }

                if (type.Fields.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("#### 🗄️ Fields");
                    foreach (var field in type.Fields)
                        sb.AppendLine($"- `{field.Modifiers} {field.Type} {string.Join(", ", field.Names)}`");
                }

                if (type.Methods.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("#### ⚙️ Métodos");
                    foreach (var method in type.Methods)
                    {
                        var asyncMark = method.IsAsync ? "async " : "";
                        var parms = string.Join(", ", method.Parameters.Select(p =>
                            p.Default != null ? $"{p.Type} {p.Name} = {p.Default}" : $"{p.Type} {p.Name}"));

                        sb.AppendLine($"- `{method.Modifiers} {asyncMark}{method.ReturnType} {method.Name}({parms})` — {method.LinesOfCode} linhas");

                        if (!string.IsNullOrWhiteSpace(method.XmlSummary))
                            sb.AppendLine($"  > {method.XmlSummary}");

                        foreach (var call in method.Calls)
                            sb.AppendLine($"  - calls: `{call}`");
                    }
                }

                sb.AppendLine();
            }
        }

        var filePath = Path.Combine(outputPath, "oracle.md");
        await File.WriteAllTextAsync(filePath, sb.ToString());
        logger.LogInformation("✔ oracle.md criado");
    }

    /// <summary>
    /// Formato compacto otimizado para colar como contexto em um LLM.
    /// </summary>
    static async Task GenerateLlmContextAsync(ProjectOracle project, string outputPath, ILogger logger)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## CODEBASE CONTEXT");
        sb.AppendLine($"Project  : {project.RootPath}");
        sb.AppendLine($"Generated: {project.GeneratedAt:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine($"Files: {project.TotalFiles} | Types: {project.TotalTypes} | Methods: {project.TotalMethods} | Properties: {project.TotalProperties}");
        sb.AppendLine();

        foreach (var file in project.Files)
        {
            foreach (var type in file.Types)
            {
                sb.Append($"[{type.Kind}] {type.Namespace}.{type.Name}");
                if (type.BaseTypes.Count > 0)
                    sb.Append($" : {string.Join(", ", type.BaseTypes)}");
                sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(type.XmlSummary))
                    sb.AppendLine($"  // {type.XmlSummary}");

                foreach (var prop in type.Properties)
                    sb.AppendLine($"  prop {prop.Modifiers} {prop.Type} {prop.Name}");

                foreach (var method in type.Methods)
                {
                    var parms = string.Join(", ", method.Parameters.Select(p => $"{p.Type} {p.Name}"));
                    var asyncMark = method.IsAsync ? "async " : "";
                    sb.AppendLine($"  {method.Modifiers} {asyncMark}{method.ReturnType} {method.Name}({parms})");
                    if (!string.IsNullOrWhiteSpace(method.XmlSummary))
                        sb.AppendLine($"    // {method.XmlSummary}");
                }

                sb.AppendLine();
            }
        }

        var filePath = Path.Combine(outputPath, "oracle_llm_context.txt");
        await File.WriteAllTextAsync(filePath, sb.ToString());
        logger.LogInformation("✔ oracle_llm_context.txt criado");
    }
}

// ══════════════════════════════════════════════════════════════
//  MODELS
// ══════════════════════════════════════════════════════════════

class ProjectOracle
{
    public string RootPath { get; set; } = "";
    public DateTime GeneratedAt { get; set; }
    public int TotalFiles { get; set; }
    public int TotalTypes { get; set; }
    public int TotalMethods { get; set; }
    public int TotalProperties { get; set; }
    public List<FileOracle> Files { get; set; } = [];
}

class FileOracle
{
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public List<string> Usings { get; set; } = [];
    public List<TypeOracle> Types { get; set; } = [];
}

class TypeOracle
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string Modifiers { get; set; } = "";
    public string? XmlSummary { get; set; }
    public List<string> BaseTypes { get; set; } = [];
    public List<string> Attributes { get; set; } = [];
    public List<ConstructorOracle> Constructors { get; set; } = [];
    public List<PropertyOracle> Properties { get; set; } = [];
    public List<FieldOracle> Fields { get; set; } = [];
    public List<MethodOracle> Methods { get; set; } = [];
}

class MethodOracle
{
    public string Name { get; set; } = "";
    public string ReturnType { get; set; } = "";
    public string Modifiers { get; set; } = "";
    public bool IsAsync { get; set; }
    public int LinesOfCode { get; set; }
    public string? XmlSummary { get; set; }
    public List<string> Attributes { get; set; } = [];
    public List<ParameterOracle> Parameters { get; set; } = [];
    public List<string> Calls { get; set; } = [];
}

class PropertyOracle
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Modifiers { get; set; } = "";
    public bool HasGetter { get; set; }
    public bool HasSetter { get; set; }
    public string? XmlSummary { get; set; }
    public List<string> Attributes { get; set; } = [];
}

class FieldOracle
{
    public List<string> Names { get; set; } = [];
    public string Type { get; set; } = "";
    public string Modifiers { get; set; } = "";
}

class ConstructorOracle
{
    public string Modifiers { get; set; } = "";
    public string? XmlSummary { get; set; }
    public List<ParameterOracle> Parameters { get; set; } = [];
}

class ParameterOracle
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Default { get; set; }
}

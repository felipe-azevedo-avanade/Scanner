using System.Collections.Concurrent;
using System.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

// ══════════════════════════════════════════════════════════════
//  ENTRY POINT
// ══════════════════════════════════════════════════════════════

class Program
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(b =>
            b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        var logger = loggerFactory.CreateLogger<Program>();

        try
        {
            var config = LoadConfig(logger);
            var watchMode = args.Contains("--watch");
            var solutionMode = args.Contains("--solution");
            var outputMode = ParseOutputMode(args);
            var impactTarget = args.FirstOrDefault(a => a.StartsWith("--impact="))?.Split('=')[1];
            var llmTarget = args.FirstOrDefault(a => a.StartsWith("--target="))?.Split('=')[1] ?? "all";
            var briefing = args.Contains("--briefing");

            logger.LogInformation("Modo     : {Mode}", watchMode ? "Watch" : briefing ? "Briefing" : impactTarget != null ? $"Impact({impactTarget})" : "Normal");
            logger.LogInformation("Output   : {Out}", outputMode);

            if (watchMode)
                await RunWatchModeAsync(config, outputMode, logger);
            else if (solutionMode)
                await RunSolutionModeAsync(config, outputMode, logger);
            else
            {
                var project = await ScanProjectAsync(config, logger);

                // ── Análises avançadas ────────────────────────────
                var intelligence = BuildIntelligence(project, config);
                project.Intelligence = intelligence;

                if (briefing)
                {
                    PrintBriefing(project, intelligence);
                }
                else if (impactTarget != null)
                {
                    PrintImpactReport(project, intelligence, impactTarget);
                }
                else
                {
                    await GenerateOutputsAsync(project, config, outputMode, llmTarget, logger);
                }
            }

            logger.LogInformation("✔ Oracle Pack finalizado. Arquivos em: {Path}", config.OutputPath);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Falha fatal.");
        }

        Console.WriteLine("\nPressione ENTER para sair...");
        Console.ReadLine();
    }

    // ══════════════════════════════════════════════════════════════
    //  CONFIG
    // ══════════════════════════════════════════════════════════════

    static AppConfig LoadConfig(ILogger logger)
    {
        var projectPath = ConfigurationManager.AppSettings["ProjectPath"]
            ?? throw new InvalidOperationException("App.config não contém 'ProjectPath'.");
        var outputPath = ConfigurationManager.AppSettings["LogOutputPath"]
            ?? throw new InvalidOperationException("App.config não contém 'LogOutputPath'.");

        var ignoredDirs = (ConfigurationManager.AppSettings["IgnoredDirectories"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ignoredSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".Designer.cs", ".g.cs", ".g.i.cs", ".generated.cs", ".AssemblyInfo.cs" };
        foreach (var s in (ConfigurationManager.AppSettings["IgnoredFileSuffixes"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
            ignoredSuffixes.Add(s);

        var maxParallel = int.TryParse(ConfigurationManager.AppSettings["MaxDegreeOfParallelism"], out var v1) ? v1 : 4;
        var maxFileSizeKb = int.TryParse(ConfigurationManager.AppSettings["MaxFileSizeKb"], out var v2) ? v2 : 500;
        var maxDepth = int.TryParse(ConfigurationManager.AppSettings["MaxDirectoryDepth"], out var v3) ? v3 : 20;
        var llmMaxChars = int.TryParse(ConfigurationManager.AppSettings["LlmContextMaxChars"], out var v4) ? v4 : 80_000;
        var smellGodClass = int.TryParse(ConfigurationManager.AppSettings["SmellGodClassMethods"], out var v5) ? v5 : 20;
        var smellLongMethod = int.TryParse(ConfigurationManager.AppSettings["SmellLongMethodLines"], out var v6) ? v6 : 50;
        var smellHighCyclo = int.TryParse(ConfigurationManager.AppSettings["SmellHighCyclomaticScore"], out var v7) ? v7 : 10;
        var riskHighDeps = int.TryParse(ConfigurationManager.AppSettings["RiskHighDependents"], out var v8) ? v8 : 5;

        if (!Directory.Exists(projectPath))
            throw new DirectoryNotFoundException($"Projeto não encontrado: {projectPath}");
        Directory.CreateDirectory(outputPath);

        logger.LogInformation("ProjectPath  : {Path}", projectPath);
        logger.LogInformation("OutputPath   : {Path}", outputPath);
        logger.LogInformation("MaxParallel  : {N}", maxParallel);

        return new AppConfig(projectPath, outputPath, ignoredDirs, ignoredSuffixes,
            maxParallel, maxFileSizeKb, maxDepth, llmMaxChars,
            smellGodClass, smellLongMethod, smellHighCyclo, riskHighDeps);
    }

    static OutputMode ParseOutputMode(string[] args)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith("--mode="));
        return arg?.Split('=')[1].ToLower() switch
        {
            "fast" => OutputMode.Fast,
            "metrics" => OutputMode.Metrics,
            _ => OutputMode.Full
        };
    }

    // ══════════════════════════════════════════════════════════════
    //  WATCH MODE
    // ══════════════════════════════════════════════════════════════

    static async Task RunWatchModeAsync(AppConfig config, OutputMode mode, ILogger logger)
    {
        logger.LogInformation("👁️  Watch mode ativo em: {Path}", config.ProjectPath);
        var project = await ScanProjectAsync(config, logger);
        project.Intelligence = BuildIntelligence(project, config);
        await GenerateOutputsAsync(project, config, mode, "all", logger);

        var isScanning = false;
        System.Timers.Timer? debounce = null;

        using var watcher = new FileSystemWatcher(config.ProjectPath, "*.cs")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
        };

        void OnChanged(object _, FileSystemEventArgs e)
        {
            if (isScanning) return;
            debounce?.Stop(); debounce?.Dispose();
            debounce = new System.Timers.Timer(1000) { AutoReset = false };
            debounce.Elapsed += async (_, _) =>
            {
                if (isScanning) return;
                isScanning = true;
                try
                {
                    logger.LogInformation("🔄 Alteração: {File}", Path.GetFileName(e.FullPath));
                    var p = await ScanProjectAsync(config, logger);
                    p.Intelligence = BuildIntelligence(p, config);
                    await GenerateOutputsAsync(p, config, mode, "all", logger);
                }
                finally { isScanning = false; }
            };
            debounce.Start();
        }

        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.EnableRaisingEvents = true;
        logger.LogInformation("Watching... (ENTER para sair)");
        await Task.Run(() => Console.ReadLine());
    }

    // ══════════════════════════════════════════════════════════════
    //  SOLUTION MODE
    // ══════════════════════════════════════════════════════════════

    static async Task RunSolutionModeAsync(AppConfig config, OutputMode mode, ILogger logger)
    {
        var csprojFiles = Directory.GetFiles(config.ProjectPath, "*.csproj", SearchOption.AllDirectories).ToList();
        logger.LogInformation("Projetos encontrados: {Count}", csprojFiles.Count);

        var allProjects = new ConcurrentBag<ProjectOracle>();
        await Parallel.ForEachAsync(csprojFiles,
            new ParallelOptions { MaxDegreeOfParallelism = config.MaxParallel },
            async (csproj, _) =>
            {
                var projDir = Path.GetDirectoryName(csproj)!;
                var projConfig = config with { ProjectPath = projDir };
                var p = await ScanProjectAsync(projConfig, logger);
                p.ProjectName = Path.GetFileNameWithoutExtension(csproj);
                p.Intelligence = BuildIntelligence(p, projConfig);
                allProjects.Add(p);
            });

        var solution = new SolutionOracle
        {
            RootPath = config.ProjectPath,
            GeneratedAt = DateTime.Now,
            TotalProjects = allProjects.Count,
            Projects = allProjects.OrderBy(p => p.ProjectName).ToList()
        };
        solution.DependencyMap = BuildDependencyMap(solution.Projects);

        var sb = new StringBuilder();
        sb.AppendLine("# Oracle Pack — Solution");
        sb.AppendLine($"> {solution.GeneratedAt:dd/MM/yyyy HH:mm:ss}  |  Root: `{solution.RootPath}`  |  Projetos: {solution.TotalProjects}");
        sb.AppendLine();
        sb.AppendLine("## 🗺️ Mapa de Dependências");
        sb.AppendLine("```");
        foreach (var (proj, deps) in solution.DependencyMap)
            sb.AppendLine(deps.Count == 0
                ? $"{proj} → (sem dependências internas)"
                : string.Join("\n", deps.Select(d => $"{proj} → {d}")));
        sb.AppendLine("```");
        sb.AppendLine();
        foreach (var p in solution.Projects)
        {
            sb.AppendLine($"## 📦 {p.ProjectName}");
            sb.AppendLine($"- Arquivos: {p.TotalFiles} | Tipos: {p.TotalTypes} | Métodos: {p.TotalMethods}");
            sb.AppendLine($"- Saúde: {p.Intelligence?.ProjectHealthScore ?? 0}/100");
            sb.AppendLine($"- Smells: {p.CodeSmells.Count(s => s.Severity == Severity.Error)} erros, {p.CodeSmells.Count(s => s.Severity == Severity.Warning)} warnings");
            sb.AppendLine();
            var projOutput = Path.Combine(config.OutputPath, p.ProjectName);
            Directory.CreateDirectory(projOutput);
            await GenerateOutputsAsync(p, config with { OutputPath = projOutput }, mode, "all", logger);
        }

        await File.WriteAllTextAsync(Path.Combine(config.OutputPath, "oracle_solution.md"), sb.ToString());
        logger.LogInformation("✔ oracle_solution.md criado");
    }

    static Dictionary<string, List<string>> BuildDependencyMap(List<ProjectOracle> projects)
    {
        var nsToProject = projects
            .SelectMany(p => p.Files.SelectMany(f => f.Types).Select(t => (t.Namespace, p.ProjectName)))
            .GroupBy(x => x.Namespace)
            .ToDictionary(g => g.Key, g => g.First().ProjectName);
        return projects.ToDictionary(
            p => p.ProjectName,
            p => p.Files.SelectMany(f => f.Usings).Distinct()
                .Where(ns => nsToProject.TryGetValue(ns, out var owner) && owner != p.ProjectName)
                .Select(ns => nsToProject[ns]).Distinct().OrderBy(x => x).ToList());
    }

    // ══════════════════════════════════════════════════════════════
    //  SCAN — incremental + filtros robustos
    // ══════════════════════════════════════════════════════════════

    static async Task<ProjectOracle> ScanProjectAsync(AppConfig config, ILogger logger)
    {
        var stateFile = Path.Combine(config.OutputPath, ".oracle_state.json");
        var stateCache = LoadStateCache(stateFile);

        var csFiles = Directory.GetFiles(config.ProjectPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => PassesFilters(f, config)).ToList();

        logger.LogInformation("Arquivos .cs elegíveis: {Count}", csFiles.Count);

        var bag = new ConcurrentBag<FileOracle>();
        var processed = 0; var skipped = 0;

        await Parallel.ForEachAsync(csFiles,
            new ParallelOptions { MaxDegreeOfParallelism = config.MaxParallel },
            async (filePath, _) =>
            {
                var fileHash = ComputeFileHash(filePath);
                if (stateCache.TryGetValue(filePath, out var cached) && cached.Hash == fileHash)
                {
                    bag.Add(cached.FileOracle);
                    Interlocked.Increment(ref skipped);
                }
                else
                {
                    var fo = await ParseFileAsync(filePath);
                    if (fo is not null) { bag.Add(fo); stateCache[filePath] = new CacheEntry(fileHash, fo); }
                }
                var count = Interlocked.Increment(ref processed);
                if (count % 20 == 0 || count == csFiles.Count)
                    logger.LogDebug("Progresso: {Done}/{Total} ({Skip} cache)", count, csFiles.Count, skipped);
            });

        await SaveStateCacheAsync(stateFile, stateCache);
        var files = bag.OrderBy(f => f.FileName).ToList();
        var smells = DetectCodeSmells(files, config);
        logger.LogInformation("Cache hits: {Skip}/{Total} | Smells: {S}", skipped, csFiles.Count, smells.Count);

        return new ProjectOracle
        {
            RootPath = config.ProjectPath,
            ProjectName = Path.GetFileName(config.ProjectPath),
            GeneratedAt = DateTime.Now,
            TotalFiles = files.Count,
            TotalTypes = files.Sum(f => f.Types.Count),
            TotalMethods = files.Sum(f => f.Types.Sum(t => t.Methods.Count)),
            TotalProperties = files.Sum(f => f.Types.Sum(t => t.Properties.Count)),
            Files = files,
            CodeSmells = smells
        };
    }

    static bool PassesFilters(string filePath, AppConfig config)
    {
        if (config.IgnoredDirectories.Any(d =>
            filePath.Contains(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar,
                              StringComparison.OrdinalIgnoreCase))) return false;
        var fn = Path.GetFileName(filePath);
        if (config.IgnoredFileSuffixes.Any(s => fn.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return false;
        var rootParts = config.ProjectPath.Split(Path.DirectorySeparatorChar).Length;
        var fileParts = filePath.Split(Path.DirectorySeparatorChar).Length;
        if ((fileParts - rootParts) > config.MaxDepth) return false;
        if (new FileInfo(filePath).Length / 1024 > config.MaxFileSizeKb) return false;
        return true;
    }

    static Dictionary<string, CacheEntry> LoadStateCache(string stateFile)
    {
        try
        {
            return File.Exists(stateFile)
            ? JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(stateFile), JsonOpts) ?? []
            : [];
        }
        catch { return []; }
    }

    static async Task SaveStateCacheAsync(string stateFile, Dictionary<string, CacheEntry> cache)
    {
        try { await File.WriteAllTextAsync(stateFile, JsonSerializer.Serialize(cache, JsonOpts)); }
        catch { }
    }

    static string ComputeFileHash(string filePath)
    {
        using var sha = SHA256.Create(); using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(stream))[..12];
    }

    // ══════════════════════════════════════════════════════════════
    //  PARSING — Roslyn
    // ══════════════════════════════════════════════════════════════

    static async Task<FileOracle?> ParseFileAsync(string filePath)
    {
        var code = await File.ReadAllTextAsync(filePath);
        var tree = CSharpSyntaxTree.ParseText(code);
        if (await tree.GetRootAsync() is not CompilationUnitSyntax root) return null;

        var fo = new FileOracle
        {
            FileName = Path.GetFileName(filePath),
            RelativePath = filePath,
            Usings = root.Usings.Select(u => u.Name?.ToString() ?? "").ToList(),
            Types = []
        };
        foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            fo.Types.Add(ParseType(td));
        return fo.Types.Count > 0 ? fo : null;
    }

    static TypeOracle ParseType(TypeDeclarationSyntax td) => new()
    {
        Name = td.Identifier.Text,
        Kind = td.Keyword.Text,
        Namespace = GetNamespace(td),
        Modifiers = td.Modifiers.ToString(),
        XmlSummary = GetXmlSummary(td),
        BaseTypes = td.BaseList?.Types.Select(t => t.ToString()).ToList() ?? [],
        Attributes = GetAttributes(td.AttributeLists),
        Constructors = td.Members.OfType<ConstructorDeclarationSyntax>().Select(ParseConstructor).ToList(),
        Properties = td.Members.OfType<PropertyDeclarationSyntax>().Select(ParseProperty).ToList(),
        Fields = td.Members.OfType<FieldDeclarationSyntax>().Select(ParseField).ToList(),
        Methods = td.Members.OfType<MethodDeclarationSyntax>().Select(ParseMethod).ToList()
    };

    static MethodOracle ParseMethod(MethodDeclarationSyntax m) => new()
    {
        Name = m.Identifier.Text,
        ReturnType = m.ReturnType.ToString(),
        Modifiers = m.Modifiers.ToString(),
        IsAsync = m.Modifiers.Any(x => x.IsKind(SyntaxKind.AsyncKeyword)),
        LinesOfCode = CountLines(m),
        CyclomaticComplexity = ComputeCyclomaticComplexity(m),
        XmlSummary = GetXmlSummary(m),
        Attributes = GetAttributes(m.AttributeLists),
        Parameters = m.ParameterList.Parameters.Select(p => new ParameterOracle
        { Name = p.Identifier.Text, Type = p.Type?.ToString() ?? "?", Default = p.Default?.Value.ToString() }).ToList(),
        Calls = m.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(i => i.Expression.ToString()).Distinct().OrderBy(x => x).ToList()
    };

    static PropertyOracle ParseProperty(PropertyDeclarationSyntax p) => new()
    {
        Name = p.Identifier.Text,
        Type = p.Type.ToString(),
        Modifiers = p.Modifiers.ToString(),
        HasGetter = p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? false,
        HasSetter = p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) ?? false,
        XmlSummary = GetXmlSummary(p),
        Attributes = GetAttributes(p.AttributeLists)
    };

    static FieldOracle ParseField(FieldDeclarationSyntax f) => new()
    { Names = f.Declaration.Variables.Select(v => v.Identifier.Text).ToList(), Type = f.Declaration.Type.ToString(), Modifiers = f.Modifiers.ToString() };

    static ConstructorOracle ParseConstructor(ConstructorDeclarationSyntax c) => new()
    {
        Modifiers = c.Modifiers.ToString(),
        XmlSummary = GetXmlSummary(c),
        Parameters = c.ParameterList.Parameters.Select(p => new ParameterOracle
        { Name = p.Identifier.Text, Type = p.Type?.ToString() ?? "?", Default = p.Default?.Value.ToString() }).ToList()
    };

    static string GetNamespace(SyntaxNode node)
        => node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "<global>";

    static string? GetXmlSummary(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia().Select(t => t.GetStructure()).OfType<DocumentationCommentTriviaSyntax>().FirstOrDefault();
        var summary = trivia?.ChildNodes().OfType<XmlElementSyntax>().FirstOrDefault(x => x.StartTag.Name.ToString() == "summary");
        if (summary is null) return null;
        return string.Concat(summary.Content.OfType<XmlTextSyntax>().SelectMany(t => t.TextTokens).Select(t => t.ToString()))
            .Trim().Replace("\r\n", " ").Replace("\n", " ");
    }

    static List<string> GetAttributes(SyntaxList<AttributeListSyntax> lists)
        => lists.SelectMany(al => al.Attributes).Select(a => a.ToString()).ToList();

    static int CountLines(SyntaxNode node)
    { var span = node.GetLocation().GetLineSpan(); return span.EndLinePosition.Line - span.StartLinePosition.Line + 1; }

    static int ComputeCyclomaticComplexity(MethodDeclarationSyntax method)
        => 1 + method.DescendantNodes().Count(n =>
            n is IfStatementSyntax ||
            n is ForStatementSyntax ||
            n is ForEachStatementSyntax ||
            n is WhileStatementSyntax ||
            n is DoStatementSyntax ||
            n is SwitchSectionSyntax ||
            n is CatchClauseSyntax ||
            n is ConditionalExpressionSyntax ||
            (n is BinaryExpressionSyntax b &&
                (b.IsKind(SyntaxKind.LogicalAndExpression) ||
                 b.IsKind(SyntaxKind.LogicalOrExpression))));

    // ══════════════════════════════════════════════════════════════
    //  CODE SMELLS
    // ══════════════════════════════════════════════════════════════

    static List<CodeSmell> DetectCodeSmells(List<FileOracle> files, AppConfig config)
    {
        var smells = new List<CodeSmell>();
        foreach (var file in files)
            foreach (var type in file.Types)
            {
                if (type.Methods.Count >= config.SmellGodClassMethods)
                    smells.Add(new(file.FileName, type.Name, "God Class", $"{type.Methods.Count} métodos — considere dividir.", Severity.Warning));
                if (type.Methods.Count > 0 && type.Methods.All(m => m.XmlSummary is null) && type.XmlSummary is null)
                    smells.Add(new(file.FileName, type.Name, "Sem documentação", "Nenhum XmlDoc encontrado.", Severity.Info));
                if (file.Usings.Count > 15)
                    smells.Add(new(file.FileName, type.Name, "Alto acoplamento", $"{file.Usings.Count} usings.", Severity.Warning));
                foreach (var m in type.Methods)
                {
                    if (m.LinesOfCode >= config.SmellLongMethodLines)
                        smells.Add(new(file.FileName, type.Name, "Método longo", $"{m.Name}() tem {m.LinesOfCode} linhas.", Severity.Warning, m.Name));
                    if (m.CyclomaticComplexity >= config.SmellHighCyclomaticScore)
                        smells.Add(new(file.FileName, type.Name, "Alta complexidade", $"{m.Name}() CC={m.CyclomaticComplexity}.", Severity.Error, m.Name));
                    if (m.Calls.Count > 20)
                        smells.Add(new(file.FileName, type.Name, "Método sobrecarregado", $"{m.Name}() faz {m.Calls.Count} chamadas distintas.", Severity.Info, m.Name));
                }
            }
        return smells.OrderByDescending(s => s.Severity).ThenBy(s => s.FileName).ToList();
    }

    // ══════════════════════════════════════════════════════════════
    //  INTELLIGENCE ENGINE  ← v4 CORE
    // ══════════════════════════════════════════════════════════════

    static ProjectIntelligence BuildIntelligence(ProjectOracle project, AppConfig config)
    {
        var allMethods = project.Files
            .SelectMany(f => f.Types.Select(t => (File: f, Type: t)))
            .SelectMany(x => x.Type.Methods.Select(m => (x.File, x.Type, Method: m)))
            .ToList();

        // ── Índice global de chamadas ─────────────────────────────
        // Mapeia "NomeCurto" e "Tipo.Nome" → lista de quem chama
        var callerIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, type, method) in allMethods)
        {
            foreach (var call in method.Calls)
            {
                // call pode ser "this.Save", "service.Render", "Save", etc.
                var key = call.Contains('.') ? call.Split('.').Last() : call;
                if (!callerIndex.ContainsKey(key)) callerIndex[key] = [];
                callerIndex[key].Add($"{type.Name}.{method.Name}");
            }
        }

        // ── 1. Mapa de risco ──────────────────────────────────────
        var riskMap = new List<RiskEntry>();
        foreach (var (file, type, method) in allMethods)
        {
            var dependents = callerIndex.TryGetValue(method.Name, out var callers) ? callers.Distinct().Count() : 0;
            var hasSmell = project.CodeSmells.Any(s => s.TypeName == type.Name && s.MethodName == method.Name);
            var noDoc = method.XmlSummary is null;
            var score = ComputeRiskScore(method.CyclomaticComplexity, method.LinesOfCode, dependents, hasSmell, noDoc);
            var level = score >= 70 ? RiskLevel.High : score >= 40 ? RiskLevel.Medium : RiskLevel.Low;

            riskMap.Add(new RiskEntry(
                file.FileName, type.Name, method.Name,
                score, level, dependents,
                method.CyclomaticComplexity, method.LinesOfCode, noDoc, hasSmell));
        }

        // ── 2. Código morto ───────────────────────────────────────
        var allDeclared = allMethods
            .Select(x => (Key: x.Method.Name, Full: $"{x.Type.Name}.{x.Method.Name}",
                          IsPublic: x.Method.Modifiers.Contains("public"), x.File.FileName, x.Type.Name))
            .ToList();

        var allCalledKeys = callerIndex.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var deadCode = allDeclared
            .Where(d => !allCalledKeys.Contains(d.Key) && d.IsPublic == false) // privados nunca chamados = morto
            .Select(d => new DeadCodeEntry(d.FileName, d.Name, d.Full, DeadCodeConfidence.High))
            .ToList();

        // Públicos nunca chamados internamente = possível morto (pode ser API externa)
        var possiblyDead = allDeclared
            .Where(d => !allCalledKeys.Contains(d.Key) && d.IsPublic
                && !d.Key.Equals("Main", StringComparison.OrdinalIgnoreCase))
            .Select(d => new DeadCodeEntry(d.FileName, d.Name, d.Full, DeadCodeConfidence.Low))
            .ToList();

        deadCode.AddRange(possiblyDead);

        // ── 3. Grafo de impacto ────────────────────────────────────
        var impactGraph = new Dictionary<string, ImpactNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, type, method) in allMethods)
        {
            var key = $"{type.Name}.{method.Name}";
            if (!impactGraph.ContainsKey(key))
                impactGraph[key] = new ImpactNode(file.FileName, type.Name, method.Name);

            foreach (var call in method.Calls)
            {
                var callKey = call.Contains('.') ? call : call;
                impactGraph[key].Calls.Add(callKey);
            }
        }
        // Preenche quem é chamado por quem (upstream)
        foreach (var node in impactGraph.Values)
            foreach (var call in node.Calls)
            {
                var shortCall = call.Contains('.') ? call.Split('.').Last() : call;
                var target = impactGraph.Values.FirstOrDefault(n =>
                    n.MethodName.Equals(shortCall, StringComparison.OrdinalIgnoreCase));
                target?.CalledBy.Add($"{node.TypeName}.{node.MethodName}");
            }

        // ── 4. Duplicação estrutural ─────────────────────────────
        var duplicates = new List<DuplicateEntry>();
        var methodList = allMethods.ToList();
        for (int i = 0; i < methodList.Count; i++)
            for (int j = i + 1; j < methodList.Count; j++)
            {
                var a = methodList[i].Method;
                var b = methodList[j].Method;
                if (a.ReturnType != b.ReturnType) continue;
                if (a.Parameters.Count != b.Parameters.Count) continue;
                var sameParams = a.Parameters.Zip(b.Parameters, (p1, p2) => p1.Type == p2.Type).All(x => x);
                if (!sameParams) continue;
                // Mede similaridade de calls
                var intersection = a.Calls.Intersect(b.Calls, StringComparer.OrdinalIgnoreCase).Count();
                var union = a.Calls.Union(b.Calls, StringComparer.OrdinalIgnoreCase).Count();
                var similarity = union == 0 ? 0.0 : (double)intersection / union;
                if (similarity >= 0.6 && a.Calls.Count >= 2)
                    duplicates.Add(new DuplicateEntry(
                        methodList[i].File.FileName, methodList[i].Type.Name, a.Name,
                        methodList[j].File.FileName, methodList[j].Type.Name, b.Name,
                        (int)(similarity * 100)));
            }

        // ── 5. Sugestão de extração de método ────────────────────
        var extractionSuggestions = new List<ExtractionSuggestion>();
        foreach (var (file, type, method) in allMethods.Where(x => x.Method.CyclomaticComplexity >= 6))
        {
            var clusters = ClusterCalls(method.Calls);
            if (clusters.Count >= 2)
                extractionSuggestions.Add(new ExtractionSuggestion(
                    file.FileName, type.Name, method.Name,
                    method.CyclomaticComplexity, method.LinesOfCode, clusters));
        }

        // ── 6. Acoplamento temporal ───────────────────────────────
        var temporalCoupling = new List<TemporalCouplingEntry>();
        var callSequences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, _, method) in allMethods)
        {
            for (int i = 0; i < method.Calls.Count - 1; i++)
            {
                var pair = $"{method.Calls[i]} → {method.Calls[i + 1]}";
                callSequences[pair] = callSequences.GetValueOrDefault(pair) + 1;
            }
        }
        foreach (var (pair, count) in callSequences.Where(x => x.Value >= 3))
        {
            var parts = pair.Split(" → ");
            temporalCoupling.Add(new TemporalCouplingEntry(parts[0], parts[1], count));
        }

        // ── 7. Fingerprint de arquitetura ────────────────────────
        var archFingerprint = DetectArchitectureFingerprint(project);

        // ── 8. Score de saúde do projeto ─────────────────────────
        var healthScore = ComputeProjectHealthScore(project, riskMap, deadCode);

        // ── 9. Gerador de esqueleto de testes (estrutural) ───────
        var testSuggestions = new List<TestSuggestion>();
        foreach (var (file, type, method) in allMethods.Where(x => x.Method.CyclomaticComplexity >= 3))
        {
            var cases = GenerateTestCases(method);
            testSuggestions.Add(new TestSuggestion(file.FileName, type.Name, method.Name, method.CyclomaticComplexity, cases));
        }

        return new ProjectIntelligence
        {
            RiskMap = riskMap.OrderByDescending(r => r.Score).ToList(),
            DeadCode = deadCode.OrderByDescending(d => d.Confidence).ThenBy(d => d.FileName).ToList(),
            ImpactGraph = impactGraph,
            Duplicates = duplicates.OrderByDescending(d => d.SimilarityPercent).ToList(),
            ExtractionSuggestions = extractionSuggestions.OrderByDescending(e => e.CurrentCC).ToList(),
            TemporalCoupling = temporalCoupling.OrderByDescending(t => t.OccurrenceCount).ToList(),
            ArchFingerprint = archFingerprint,
            ProjectHealthScore = healthScore,
            TestSuggestions = testSuggestions.OrderByDescending(t => t.CC).ToList()
        };
    }

    // ── Helpers da Intelligence ───────────────────────────────────

    static int ComputeRiskScore(int cc, int lines, int dependents, bool hasSmell, bool noDoc)
    {
        var score = 0;
        score += cc >= 15 ? 35 : cc >= 10 ? 25 : cc >= 6 ? 15 : 5;
        score += lines >= 100 ? 20 : lines >= 50 ? 12 : lines >= 30 ? 6 : 0;
        score += dependents >= 8 ? 25 : dependents >= 5 ? 15 : dependents >= 2 ? 8 : 0;
        score += hasSmell ? 15 : 0;
        score += noDoc ? 5 : 0;
        return Math.Min(score, 100);
    }

    static List<List<string>> ClusterCalls(List<string> calls)
    {
        // Agrupa calls em blocos de até 5 calls consecutivas por prefixo (heurística simples)
        if (calls.Count < 4) return [];
        var clusters = new List<List<string>>();
        var current = new List<string>();
        string? lastPrefix = null;

        foreach (var call in calls)
        {
            var prefix = call.Contains('.') ? call.Split('.')[0] : "_";
            if (lastPrefix != null && prefix != lastPrefix && current.Count >= 2)
            { clusters.Add(new List<string>(current)); current.Clear(); }
            current.Add(call);
            lastPrefix = prefix;
        }
        if (current.Count >= 2) clusters.Add(current);
        return clusters.Count >= 2 ? clusters : [];
    }

    static ArchitectureFingerprint DetectArchitectureFingerprint(ProjectOracle project)
    {
        var namespaces = project.Files.SelectMany(f => f.Types.Select(t => t.Namespace)).Distinct().ToList();
        var allTypes = project.Files.SelectMany(f => f.Types).ToList();
        var interfaces = allTypes.Where(t => t.Kind == "interface").ToList();
        var concreteTypes = allTypes.Where(t => t.Kind == "class").ToList();

        // Detectar padrão
        var hasApplication = namespaces.Any(n => n.Contains("Application", StringComparison.OrdinalIgnoreCase));
        var hasDomain = namespaces.Any(n => n.Contains("Domain", StringComparison.OrdinalIgnoreCase));
        var hasInfrastructure = namespaces.Any(n => n.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase));
        var hasPresentation = namespaces.Any(n => n.Contains("Presentation", StringComparison.OrdinalIgnoreCase)
                                                 || n.Contains("Api", StringComparison.OrdinalIgnoreCase)
                                                 || n.Contains("Web", StringComparison.OrdinalIgnoreCase));

        var pattern = (hasApplication, hasDomain, hasInfrastructure) switch
        {
            (true, true, true) => "Clean Architecture",
            (false, true, true) => "Layered Architecture",
            (false, false, true) => "Infrastructure-heavy",
            _ => "Flat / Não identificado"
        };

        // Detectar padrões de design
        var detectedPatterns = new List<string>();

        // Strategy — interface com múltiplas implementações
        foreach (var iface in interfaces)
        {
            var implementors = concreteTypes.Where(t =>
                t.BaseTypes.Any(b => b.Contains(iface.Name))).ToList();
            if (implementors.Count >= 2)
                detectedPatterns.Add($"Strategy: {iface.Name} ({implementors.Count} implementações)");
        }

        // Repository — interface com Get/Save/Delete
        var repoInterfaces = interfaces.Where(i =>
            i.Methods.Any(m => m.Name.StartsWith("Get", StringComparison.OrdinalIgnoreCase)) &&
            i.Methods.Any(m => m.Name.StartsWith("Save", StringComparison.OrdinalIgnoreCase) ||
                               m.Name.StartsWith("Add", StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var repo in repoInterfaces)
            detectedPatterns.Add($"Repository: {repo.Name}");

        // Factory — método estático que retorna interface ou classe abstrata
        var factories = concreteTypes
            .SelectMany(t => t.Methods.Where(m =>
                m.Modifiers.Contains("static") &&
                interfaces.Any(i => m.ReturnType.Contains(i.Name))))
            .ToList();
        if (factories.Any())
            detectedPatterns.Add($"Factory: {factories.Count} método(s) fábrica detectado(s)");

        // Violações arquiteturais (Infrastructure chamando Domain diretamente sem abstração)
        var violations = new List<string>();
        foreach (var file in project.Files)
            foreach (var type in file.Types.Where(t => t.Namespace.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase)))
                foreach (var method in type.Methods)
                {
                    var domainCalls = method.Calls.Where(c =>
                        project.Files.SelectMany(f => f.Types)
                            .Any(t => t.Namespace.Contains("Domain", StringComparison.OrdinalIgnoreCase)
                                   && c.Contains(t.Name))).ToList();
                    if (domainCalls.Any())
                        violations.Add($"{type.Name}.{method.Name}() chama Domain diretamente: {string.Join(", ", domainCalls)}");
                }

        return new ArchitectureFingerprint(pattern, detectedPatterns, violations, interfaces.Count, concreteTypes.Count);
    }

    static int ComputeProjectHealthScore(ProjectOracle project, List<RiskEntry> riskMap, List<DeadCodeEntry> deadCode)
    {
        var score = 100;
        var errors = project.CodeSmells.Count(s => s.Severity == Severity.Error);
        var warnings = project.CodeSmells.Count(s => s.Severity == Severity.Warning);
        var highRisk = riskMap.Count(r => r.Level == RiskLevel.High);
        var allMethods = project.Files.SelectMany(f => f.Types.SelectMany(t => t.Methods)).ToList();
        var docCoverage = allMethods.Count == 0 ? 100 :
            (int)(allMethods.Count(m => m.XmlSummary != null) * 100.0 / allMethods.Count);

        score -= errors * 10;
        score -= warnings * 4;
        score -= highRisk * 5;
        score -= deadCode.Count(d => d.Confidence == DeadCodeConfidence.High) * 3;
        score -= Math.Max(0, 70 - docCoverage) / 5;
        return Math.Max(0, Math.Min(100, score));
    }

    static List<string> GenerateTestCases(MethodOracle method)
    {
        var cases = new List<string>();
        cases.Add($"{method.Name}_Should_Execute_HappyPath");
        if (method.Parameters.Any())
            cases.Add($"{method.Name}_WhenParamsAreNull_Should_Throw");
        if (method.IsAsync)
            cases.Add($"{method.Name}_WhenCancelled_Should_ThrowOperationCanceledException");
        // Adiciona casos baseado em CC
        for (int i = 1; i < Math.Min(method.CyclomaticComplexity, 8); i++)
            cases.Add($"{method.Name}_Scenario{i}_Should_ReturnExpectedResult");
        if (method.Calls.Any(c => c.Contains("throw", StringComparison.OrdinalIgnoreCase) ||
                                  c.Contains("exception", StringComparison.OrdinalIgnoreCase)))
            cases.Add($"{method.Name}_WhenExceptionThrown_Should_Handle");
        return cases;
    }

    // ══════════════════════════════════════════════════════════════
    //  BRIEFING — modo --briefing
    // ══════════════════════════════════════════════════════════════

    static void PrintBriefing(ProjectOracle project, ProjectIntelligence intel)
    {
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════");
        Console.WriteLine($"  📋 ORACLE BRIEFING — {project.ProjectName}");
        Console.WriteLine($"  {project.GeneratedAt:dd/MM/yyyy HH:mm:ss}");
        Console.WriteLine("══════════════════════════════════════════════");
        Console.WriteLine();

        var health = intel.ProjectHealthScore;
        var hIcon = health >= 80 ? "🟢" : health >= 50 ? "🟡" : "🔴";
        Console.WriteLine($"  Saúde geral: {hIcon} {health}/100");
        Console.WriteLine($"  Arquitetura: {intel.ArchFingerprint.Pattern}");
        Console.WriteLine($"  Arquivos: {project.TotalFiles}  |  Tipos: {project.TotalTypes}  |  Métodos: {project.TotalMethods}");
        Console.WriteLine();

        // Smells críticos
        var errors = project.CodeSmells.Where(s => s.Severity == Severity.Error).ToList();
        if (errors.Any())
        {
            Console.WriteLine($"  🔴 {errors.Count} problema(s) crítico(s):");
            foreach (var e in errors.Take(3))
                Console.WriteLine($"     • {e.TypeName}.{e.MethodName}: {e.Detail}");
            if (errors.Count > 3) Console.WriteLine($"     ... e mais {errors.Count - 3}");
            Console.WriteLine();
        }

        // Top risco
        var topRisk = intel.RiskMap.Where(r => r.Level == RiskLevel.High).Take(3).ToList();
        if (topRisk.Any())
        {
            Console.WriteLine($"  ⚠️  Top {topRisk.Count} método(s) de alto risco para alterar:");
            foreach (var r in topRisk)
                Console.WriteLine($"     • {r.TypeName}.{r.MethodName}() — Score {r.Score}/100, {r.Dependents} dependentes");
            Console.WriteLine();
        }

        // Código morto
        var deadHigh = intel.DeadCode.Where(d => d.Confidence == DeadCodeConfidence.High).ToList();
        if (deadHigh.Any())
        {
            Console.WriteLine($"  🪦 {deadHigh.Count} método(s) provavelmente morto(s) (privados nunca chamados)");
            Console.WriteLine();
        }

        // Padrões detectados
        if (intel.ArchFingerprint.DetectedPatterns.Any())
        {
            Console.WriteLine("  🏛️  Padrões detectados:");
            foreach (var p in intel.ArchFingerprint.DetectedPatterns)
                Console.WriteLine($"     • {p}");
            Console.WriteLine();
        }

        // Violações
        if (intel.ArchFingerprint.ArchitectureViolations.Any())
        {
            Console.WriteLine($"  ❌ {intel.ArchFingerprint.ArchitectureViolations.Count} violação(ões) arquitetural(is)");
            foreach (var v in intel.ArchFingerprint.ArchitectureViolations.Take(2))
                Console.WriteLine($"     • {v}");
            Console.WriteLine();
        }

        // Recomendação
        Console.WriteLine("  💡 Próxima ação recomendada:");
        if (errors.Any())
            Console.WriteLine($"     Refatorar {errors.First().TypeName}.{errors.First().MethodName}() (CC crítico)");
        else if (topRisk.Any())
            Console.WriteLine($"     Documentar e cobrir com testes {topRisk.First().TypeName}.{topRisk.First().MethodName}() antes de alterar");
        else if (deadHigh.Any())
            Console.WriteLine($"     Remover código morto: {deadHigh.Count} método(s) candidato(s)");
        else
            Console.WriteLine("     Projeto em boa forma. Foco em aumentar cobertura de documentação.");

        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════");
    }

    // ══════════════════════════════════════════════════════════════
    //  IMPACT REPORT — modo --impact=NomeMetodo
    // ══════════════════════════════════════════════════════════════

    static void PrintImpactReport(ProjectOracle project, ProjectIntelligence intel, string target)
    {
        var node = intel.ImpactGraph.Values.FirstOrDefault(n =>
            n.MethodName.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            $"{n.TypeName}.{n.MethodName}".Equals(target, StringComparison.OrdinalIgnoreCase));

        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════");
        Console.WriteLine($"  🎯 IMPACT REPORT — {target}");
        Console.WriteLine("══════════════════════════════════════════════");

        if (node is null)
        {
            Console.WriteLine($"\n  ❌ Método '{target}' não encontrado no projeto.");
            Console.WriteLine("  Dica: use NomeClasse.NomeMetodo ou só NomeMetodo");
            Console.WriteLine();
            return;
        }

        var risk = intel.RiskMap.FirstOrDefault(r =>
            r.TypeName == node.TypeName && r.MethodName == node.MethodName);

        Console.WriteLine($"\n  📍 {node.FileName} › {node.TypeName}.{node.MethodName}()");
        if (risk != null)
        {
            var rIcon = risk.Level == RiskLevel.High ? "🔴" : risk.Level == RiskLevel.Medium ? "🟡" : "🟢";
            Console.WriteLine($"  Risco: {rIcon} {risk.Level} (Score {risk.Score}/100)");
            Console.WriteLine($"  CC={risk.CyclomaticComplexity}  |  {risk.LinesOfCode} linhas  |  {risk.Dependents} tipos dependentes");
        }

        Console.WriteLine();
        if (node.CalledBy.Any())
        {
            Console.WriteLine($"  ⬆️  Quem chama este método (upstream — {node.CalledBy.Count}):");
            foreach (var caller in node.CalledBy.Take(10))
                Console.WriteLine($"     ← {caller}");
            if (node.CalledBy.Count > 10) Console.WriteLine($"     ... e mais {node.CalledBy.Count - 10}");
        }
        else
            Console.WriteLine("  ⬆️  Nenhum caller interno detectado (pode ser entry point ou API pública)");

        Console.WriteLine();
        if (node.Calls.Any())
        {
            Console.WriteLine($"  ⬇️  O que este método chama (downstream — {node.Calls.Count}):");
            foreach (var call in node.Calls.Take(10))
                Console.WriteLine($"     → {call}");
            if (node.Calls.Count > 10) Console.WriteLine($"     ... e mais {node.Calls.Count - 10}");
        }

        // Cadeia de impacto transitiva (2 níveis)
        var transitiveImpact = node.CalledBy
            .SelectMany(caller => intel.ImpactGraph.Values
                .Where(n => n.CalledBy.Contains(caller))
                .Select(n => $"{n.TypeName}.{n.MethodName}"))
            .Distinct()
            .Where(x => x != $"{node.TypeName}.{node.MethodName}")
            .ToList();

        if (transitiveImpact.Any())
        {
            Console.WriteLine();
            Console.WriteLine($"  🔗 Impacto transitivo (2 níveis — {transitiveImpact.Count} tipos):");
            foreach (var t in transitiveImpact.Take(5))
                Console.WriteLine($"     ⟳ {t}");
        }

        var totalImpact = node.CalledBy.Count + transitiveImpact.Count;
        Console.WriteLine();
        Console.WriteLine($"  📊 Estimativa de impacto: {totalImpact} método(s) afetado(s) por uma mudança aqui.");
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════");
    }

    // ══════════════════════════════════════════════════════════════
    //  GERAÇÃO DE OUTPUTS
    // ══════════════════════════════════════════════════════════════

    static async Task GenerateOutputsAsync(ProjectOracle project, AppConfig config, OutputMode mode, string llmTarget, ILogger logger)
    {
        Directory.CreateDirectory(config.OutputPath);
        var tasks = new List<Task>
        {
            GenerateLlmContextAsync(project, config, llmTarget, logger)
        };

        if (mode is OutputMode.Full or OutputMode.Metrics)
        {
            tasks.Add(GenerateMetricsAsync(project, config.OutputPath, logger));
            tasks.Add(GenerateSmellsReportAsync(project, config.OutputPath, logger));
            tasks.Add(GenerateRiskMapAsync(project, config.OutputPath, logger));
            tasks.Add(GenerateDeadCodeReportAsync(project, config.OutputPath, logger));
            tasks.Add(GenerateIntelligenceReportAsync(project, config.OutputPath, logger));
        }

        if (mode == OutputMode.Full)
        {
            tasks.Add(GenerateJsonAsync(project, config.OutputPath, logger));
            tasks.Add(GenerateMarkdownAsync(project, config.OutputPath, logger));
            tasks.Add(GenerateHtmlAsync(project, config, logger));
        }

        await Task.WhenAll(tasks);
    }

    // ── JSON ──────────────────────────────────────────────────────

    static async Task GenerateJsonAsync(ProjectOracle project, string outputPath, ILogger logger)
    {
        var json = JsonSerializer.Serialize(project, JsonOpts);
        await File.WriteAllTextAsync(Path.Combine(outputPath, "oracle.json"), json);
        logger.LogInformation("✔ oracle.json ({Size} KB)", json.Length / 1024);
    }

    // ── Markdown ──────────────────────────────────────────────────

    static async Task GenerateMarkdownAsync(ProjectOracle project, string outputPath, ILogger logger)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Oracle Pack");
        sb.AppendLine($"> {project.GeneratedAt:dd/MM/yyyy HH:mm:ss}  |  `{project.RootPath}`");
        sb.AppendLine($"> Saúde: **{project.Intelligence?.ProjectHealthScore ?? 0}/100**  |  Arquitetura: **{project.Intelligence?.ArchFingerprint.Pattern ?? "?"}**");
        sb.AppendLine();
        sb.AppendLine("## 📊 Estatísticas");
        sb.AppendLine("| Métrica | Valor |"); sb.AppendLine("|---------|-------|");
        sb.AppendLine($"| Arquivos     | {project.TotalFiles}      |");
        sb.AppendLine($"| Tipos        | {project.TotalTypes}      |");
        sb.AppendLine($"| Métodos      | {project.TotalMethods}    |");
        sb.AppendLine($"| Propriedades | {project.TotalProperties} |");
        sb.AppendLine($"| Code Smells  | {project.CodeSmells.Count} |");
        sb.AppendLine($"| Alto Risco   | {project.Intelligence?.RiskMap.Count(r => r.Level == RiskLevel.High) ?? 0} |");
        sb.AppendLine($"| Código Morto | {project.Intelligence?.DeadCode.Count(d => d.Confidence == DeadCodeConfidence.High) ?? 0} (alta confiança) |");
        sb.AppendLine();

        foreach (var file in project.Files)
        {
            sb.AppendLine($"## 📄 {file.FileName}");
            if (file.Usings.Count > 0) sb.AppendLine($"*Usings: {string.Join(", ", file.Usings)}*");
            sb.AppendLine();
            foreach (var type in file.Types)
            {
                sb.AppendLine($"### {type.Kind} `{type.Namespace}.{type.Name}`");
                if (!string.IsNullOrWhiteSpace(type.XmlSummary)) sb.AppendLine($"> {type.XmlSummary}");
                sb.AppendLine($"- **Modificadores:** `{type.Modifiers}`");
                if (type.BaseTypes.Count > 0) sb.AppendLine($"- **Herda/Implementa:** {string.Join(", ", type.BaseTypes.Select(b => $"`{b}`"))}");

                if (type.Methods.Count > 0)
                {
                    sb.AppendLine(); sb.AppendLine("#### ⚙️ Métodos");
                    foreach (var m in type.Methods)
                    {
                        var risk = project.Intelligence?.RiskMap.FirstOrDefault(r => r.TypeName == type.Name && r.MethodName == m.Name);
                        var riskIcon = risk?.Level switch { RiskLevel.High => " 🔴", RiskLevel.Medium => " 🟡", _ => "" };
                        var async_ = m.IsAsync ? "async " : "";
                        var parms = string.Join(", ", m.Parameters.Select(p => $"{p.Type} {p.Name}"));
                        var ccMark = m.CyclomaticComplexity >= 10 ? $" ⚠️CC={m.CyclomaticComplexity}" : $" CC={m.CyclomaticComplexity}";
                        sb.AppendLine($"- `{m.Modifiers} {async_}{m.ReturnType} {m.Name}({parms})` — {m.LinesOfCode}L{ccMark}{riskIcon}");
                        if (!string.IsNullOrWhiteSpace(m.XmlSummary)) sb.AppendLine($"  > {m.XmlSummary}");
                    }
                }
                sb.AppendLine();
            }
        }

        await File.WriteAllTextAsync(Path.Combine(outputPath, "oracle.md"), sb.ToString());
        logger.LogInformation("✔ oracle.md criado");
    }

    // ── LLM Context — compacto + por namespace + por IA ──────────

    static async Task GenerateLlmContextAsync(ProjectOracle project, AppConfig config, string target, ILogger logger)
    {
        var llmDir = Path.Combine(config.OutputPath, "llm_context");
        Directory.CreateDirectory(llmDir);

        var byNamespace = project.Files
            .SelectMany(f => f.Types.Select(t => (f, t)))
            .GroupBy(x => x.t.Namespace).OrderBy(g => g.Key).ToList();

        // Global compacto
        var globalSb = BuildGlobalContext(project, byNamespace, config.LlmMaxChars);
        await File.WriteAllTextAsync(Path.Combine(config.OutputPath, "oracle_llm_context.txt"), globalSb);

        // Por namespace
        foreach (var group in byNamespace)
        {
            var nsSb = new StringBuilder();
            nsSb.AppendLine($"## NAMESPACE: {group.Key}");
            nsSb.AppendLine($"Project: {project.RootPath}"); nsSb.AppendLine();
            foreach (var (_, type) in group) AppendTypeContext(nsSb, type);
            var nsFile = group.Key.Replace(".", "_").Replace("<", "").Replace(">", "").Replace(" ", "") + ".txt";
            await File.WriteAllTextAsync(Path.Combine(llmDir, nsFile), nsSb.ToString());
        }

        // Formatos por IA
        if (target == "all" || target == "claude")
        {
            var claudeSb = BuildClaudeContext(project);
            await File.WriteAllTextAsync(Path.Combine(llmDir, "context_claude.txt"), claudeSb);
        }
        if (target == "all" || target == "gpt")
        {
            var gptSb = BuildGptContext(project);
            await File.WriteAllTextAsync(Path.Combine(llmDir, "context_gpt.txt"), gptSb);
        }

        logger.LogInformation("✔ oracle_llm_context.txt + {N} namespaces + formatos por IA", byNamespace.Count);
    }

    static string BuildGlobalContext(ProjectOracle project, List<IGrouping<string, (FileOracle f, TypeOracle t)>> byNamespace, int maxChars)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## CODEBASE CONTEXT");
        sb.AppendLine($"Project  : {project.RootPath}");
        sb.AppendLine($"Generated: {project.GeneratedAt:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine($"Health   : {project.Intelligence?.ProjectHealthScore ?? 0}/100  |  Arch: {project.Intelligence?.ArchFingerprint.Pattern ?? "?"}");
        sb.AppendLine($"Files: {project.TotalFiles} | Types: {project.TotalTypes} | Methods: {project.TotalMethods} | Properties: {project.TotalProperties}");
        sb.AppendLine();
        foreach (var group in byNamespace)
            foreach (var (_, type) in group)
            {
                if (sb.Length >= maxChars) { sb.AppendLine("\n[TRUNCADO — use /llm_context/ para contexto completo]"); break; }
                AppendTypeContext(sb, type);
            }
        return sb.ToString();
    }

    // Formato otimizado para Claude — hierarquia clara com contexto de risco
    static string BuildClaudeContext(ProjectOracle project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<codebase_context>");
        sb.AppendLine($"<project name=\"{project.ProjectName}\" health=\"{project.Intelligence?.ProjectHealthScore ?? 0}/100\" architecture=\"{project.Intelligence?.ArchFingerprint.Pattern ?? "?"}\"/>");
        sb.AppendLine($"<stats files=\"{project.TotalFiles}\" types=\"{project.TotalTypes}\" methods=\"{project.TotalMethods}\"/>");
        sb.AppendLine();
        foreach (var file in project.Files)
            foreach (var type in file.Types)
            {
                var risk = project.Intelligence?.RiskMap.Where(r => r.TypeName == type.Name)
                    .OrderByDescending(r => r.Score).FirstOrDefault();
                sb.AppendLine($"<type kind=\"{type.Kind}\" name=\"{type.Namespace}.{type.Name}\" risk=\"{risk?.Level.ToString() ?? "Low"}\">");
                if (!string.IsNullOrWhiteSpace(type.XmlSummary)) sb.AppendLine($"  <summary>{type.XmlSummary}</summary>");
                foreach (var m in type.Methods)
                {
                    var parms = string.Join(", ", m.Parameters.Select(p => $"{p.Type} {p.Name}"));
                    sb.AppendLine($"  <method sig=\"{m.ReturnType} {m.Name}({parms})\" cc=\"{m.CyclomaticComplexity}\" lines=\"{m.LinesOfCode}\" async=\"{m.IsAsync}\"/>");
                }
                sb.AppendLine("</type>");
            }
        sb.AppendLine("</codebase_context>");
        return sb.ToString();
    }

    // Formato otimizado para ChatGPT — listas enumeradas, mais verboso
    static string BuildGptContext(ProjectOracle project)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Project: {project.ProjectName}");
        sb.AppendLine($"Architecture: {project.Intelligence?.ArchFingerprint.Pattern ?? "?"}  |  Health: {project.Intelligence?.ProjectHealthScore ?? 0}/100");
        sb.AppendLine($"Stats: {project.TotalFiles} files, {project.TotalTypes} types, {project.TotalMethods} methods");
        sb.AppendLine();
        var grouped = project.Files.SelectMany(f => f.Types.Select(t => (f, t))).GroupBy(x => x.t.Namespace);
        foreach (var ns in grouped)
        {
            sb.AppendLine($"## Namespace: {ns.Key}");
            int i = 1;
            foreach (var (_, type) in ns)
            {
                sb.AppendLine($"{i++}. [{type.Kind}] {type.Name}");
                if (!string.IsNullOrWhiteSpace(type.XmlSummary)) sb.AppendLine($"   Description: {type.XmlSummary}");
                if (type.BaseTypes.Any()) sb.AppendLine($"   Implements: {string.Join(", ", type.BaseTypes)}");
                foreach (var m in type.Methods)
                {
                    var parms = string.Join(", ", m.Parameters.Select(p => $"{p.Type} {p.Name}"));
                    sb.AppendLine($"   - {m.ReturnType} {m.Name}({parms}) [CC={m.CyclomaticComplexity}, {m.LinesOfCode}L]");
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    static void AppendTypeContext(StringBuilder sb, TypeOracle type)
    {
        sb.Append($"[{type.Kind}] {type.Namespace}.{type.Name}");
        if (type.BaseTypes.Count > 0) sb.Append($" : {string.Join(", ", type.BaseTypes)}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(type.XmlSummary)) sb.AppendLine($"  // {type.XmlSummary}");
        foreach (var prop in type.Properties) sb.AppendLine($"  prop {prop.Modifiers} {prop.Type} {prop.Name}");
        foreach (var m in type.Methods)
        {
            var parms = string.Join(", ", m.Parameters.Select(p => $"{p.Type} {p.Name}"));
            sb.AppendLine($"  {m.Modifiers} {(m.IsAsync ? "async " : "")}{m.ReturnType} {m.Name}({parms})");
            if (!string.IsNullOrWhiteSpace(m.XmlSummary)) sb.AppendLine($"    // {m.XmlSummary}");
        }
        sb.AppendLine();
    }

    // ── Mapa de Risco ─────────────────────────────────────────────

    static async Task GenerateRiskMapAsync(ProjectOracle project, string outputPath, ILogger logger)
    {
        if (project.Intelligence is null) return;
        var sb = new StringBuilder();
        sb.AppendLine("# Oracle Pack — Mapa de Risco de Alteração");
        sb.AppendLine($"> {project.GeneratedAt:dd/MM/yyyy HH:mm:ss}  |  Use este relatório ANTES de alterar qualquer método");
        sb.AppendLine();

        foreach (var level in new[] { RiskLevel.High, RiskLevel.Medium, RiskLevel.Low })
        {
            var entries = project.Intelligence.RiskMap.Where(r => r.Level == level).ToList();
            if (!entries.Any()) continue;
            var icon = level == RiskLevel.High ? "🔴" : level == RiskLevel.Medium ? "🟡" : "🟢";
            sb.AppendLine($"## {icon} Risco {level} ({entries.Count})");
            sb.AppendLine("| Arquivo | Classe | Método | Score | CC | Linhas | Dependentes | Smell | Sem Doc |");
            sb.AppendLine("|---------|--------|--------|-------|----|--------|-------------|-------|---------|");
            foreach (var r in entries)
                sb.AppendLine($"| {r.FileName} | {r.TypeName} | {r.MethodName} | {r.Score} | {r.CyclomaticComplexity} | {r.LinesOfCode} | {r.Dependents} | {(r.HasSmell ? "⚠️" : "-")} | {(r.NoDoc ? "📝" : "-")} |");
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(Path.Combine(outputPath, "oracle_risk.md"), sb.ToString());
        logger.LogInformation("✔ oracle_risk.md criado ({High} alto risco)",
            project.Intelligence.RiskMap.Count(r => r.Level == RiskLevel.High));
    }

    // ── Código Morto ──────────────────────────────────────────────

    static async Task GenerateDeadCodeReportAsync(ProjectOracle project, string outputPath, ILogger logger)
    {
        if (project.Intelligence is null) return;
        var sb = new StringBuilder();
        sb.AppendLine("# Oracle Pack — Código Morto");
        sb.AppendLine($"> {project.GeneratedAt:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("> ⚠️ Verifique manualmente antes de remover. Chamadas via reflection, eventos ou APIs externas não são detectadas.");
        sb.AppendLine();

        var high = project.Intelligence.DeadCode.Where(d => d.Confidence == DeadCodeConfidence.High).ToList();
        var low = project.Intelligence.DeadCode.Where(d => d.Confidence == DeadCodeConfidence.Low).ToList();

        if (high.Any())
        {
            sb.AppendLine($"## 🪦 Alta Confiança — privados nunca chamados ({high.Count})");
            sb.AppendLine("| Arquivo | Classe | Método | Ação Sugerida |");
            sb.AppendLine("|---------|--------|--------|---------------|");
            foreach (var d in high)
                sb.AppendLine($"| {d.FileName} | {d.TypeName} | {d.MethodName} | Remover com segurança |");
            sb.AppendLine();
        }

        if (low.Any())
        {
            sb.AppendLine($"## 🤔 Baixa Confiança — públicos sem callers internos ({low.Count})");
            sb.AppendLine("| Arquivo | Classe | Método | Ação Sugerida |");
            sb.AppendLine("|---------|--------|--------|---------------|");
            foreach (var d in low)
                sb.AppendLine($"| {d.FileName} | {d.TypeName} | {d.MethodName} | Verificar se é API externa |");
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(Path.Combine(outputPath, "oracle_deadcode.md"), sb.ToString());
        logger.LogInformation("✔ oracle_deadcode.md criado ({H} alta, {L} baixa confiança)", high.Count, low.Count);
    }

    // ── Relatório de Inteligência consolidado ─────────────────────

    static async Task GenerateIntelligenceReportAsync(ProjectOracle project, string outputPath, ILogger logger)
    {
        if (project.Intelligence is null) return;
        var intel = project.Intelligence;
        var sb = new StringBuilder();

        sb.AppendLine("# Oracle Pack — Intelligence Report");
        sb.AppendLine($"> {project.GeneratedAt:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine();

        // Saúde
        var h = intel.ProjectHealthScore;
        sb.AppendLine($"## 💚 Score de Saúde: {h}/100 {(h >= 80 ? "🟢 Bom" : h >= 50 ? "🟡 Atenção" : "🔴 Crítico")}");
        sb.AppendLine();

        // Arquitetura
        sb.AppendLine($"## 🏛️ Fingerprint de Arquitetura: **{intel.ArchFingerprint.Pattern}**");
        sb.AppendLine($"- Interfaces: {intel.ArchFingerprint.InterfaceCount}");
        sb.AppendLine($"- Classes concretas: {intel.ArchFingerprint.ConcreteCount}");
        if (intel.ArchFingerprint.DetectedPatterns.Any())
        {
            sb.AppendLine();
            sb.AppendLine("**Padrões detectados:**");
            foreach (var p in intel.ArchFingerprint.DetectedPatterns) sb.AppendLine($"- ✅ {p}");
        }
        if (intel.ArchFingerprint.ArchitectureViolations.Any())
        {
            sb.AppendLine();
            sb.AppendLine("**Violações detectadas:**");
            foreach (var v in intel.ArchFingerprint.ArchitectureViolations) sb.AppendLine($"- ❌ {v}");
        }
        sb.AppendLine();

        // Duplicação estrutural
        if (intel.Duplicates.Any())
        {
            sb.AppendLine($"## 🔁 Duplicação Estrutural ({intel.Duplicates.Count})");
            sb.AppendLine("| Método A | Método B | Similaridade |");
            sb.AppendLine("|----------|----------|--------------|");
            foreach (var d in intel.Duplicates.Take(15))
                sb.AppendLine($"| {d.TypeNameA}.{d.MethodNameA} | {d.TypeNameB}.{d.MethodNameB} | {d.SimilarityPercent}% |");
            sb.AppendLine();
        }

        // Extração sugerida
        if (intel.ExtractionSuggestions.Any())
        {
            sb.AppendLine($"## 💡 Sugestões de Extração de Método ({intel.ExtractionSuggestions.Count})");
            foreach (var e in intel.ExtractionSuggestions.Take(10))
            {
                sb.AppendLine($"### {e.TypeName}.{e.MethodName}() — CC={e.CurrentCC}, {e.CurrentLines} linhas");
                sb.AppendLine($"Dividir em {e.SuggestedClusters.Count} métodos:");
                for (int i = 0; i < e.SuggestedClusters.Count; i++)
                    sb.AppendLine($"- Bloco {i + 1}: `{string.Join(", ", e.SuggestedClusters[i].Take(3))}`{(e.SuggestedClusters[i].Count > 3 ? "..." : "")}");
                sb.AppendLine($"- CC estimado após extração: ~{Math.Max(1, e.CurrentCC / e.SuggestedClusters.Count)}");
                sb.AppendLine();
            }
        }

        // Acoplamento temporal
        if (intel.TemporalCoupling.Any())
        {
            sb.AppendLine($"## 🔗 Acoplamento Temporal ({intel.TemporalCoupling.Count} pares)");
            sb.AppendLine("| Chamada A | Chamada B | Ocorrências | Sugestão |");
            sb.AppendLine("|-----------|-----------|-------------|----------|");
            foreach (var t in intel.TemporalCoupling.Take(10))
                sb.AppendLine($"| {t.CallA} | {t.CallB} | {t.OccurrenceCount}x | Considerar encapsular em método único |");
            sb.AppendLine();
        }

        // Testes sugeridos
        if (intel.TestSuggestions.Any())
        {
            sb.AppendLine($"## 🧪 Esqueleto de Testes Sugeridos (Top 10 por CC)");
            foreach (var ts in intel.TestSuggestions.Take(10))
            {
                sb.AppendLine($"### {ts.TypeName}.{ts.MethodName}() — CC={ts.CC} ({ts.SuggestedCases.Count} casos)");
                sb.AppendLine("```csharp");
                foreach (var c in ts.SuggestedCases)
                    sb.AppendLine($"[Fact] public void {c}() {{ }}");
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        await File.WriteAllTextAsync(Path.Combine(outputPath, "oracle_intelligence.md"), sb.ToString());
        logger.LogInformation("✔ oracle_intelligence.md criado");
    }

    // ── Métricas ──────────────────────────────────────────────────

    static async Task GenerateMetricsAsync(ProjectOracle project, string outputPath, ILogger logger)
    {
        var sb = new StringBuilder();
        var methods = project.Files.SelectMany(f => f.Types.SelectMany(t =>
            t.Methods.Select(m => (File: f.FileName, Type: t.Name, Method: m)))).ToList();

        sb.AppendLine("# Oracle Pack — Métricas");
        sb.AppendLine($"> {project.GeneratedAt:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("## 🔴 Top 10 — Complexidade Ciclomática");
        sb.AppendLine("| Arquivo | Classe | Método | CC | Linhas |");
        sb.AppendLine("|---------|--------|--------|----|--------|");
        foreach (var x in methods.OrderByDescending(x => x.Method.CyclomaticComplexity).Take(10))
            sb.AppendLine($"| {x.File} | {x.Type} | {x.Method.Name} | {x.Method.CyclomaticComplexity} | {x.Method.LinesOfCode} |");
        sb.AppendLine();
        sb.AppendLine("## 📏 Top 10 — Linhas de Código");
        sb.AppendLine("| Arquivo | Classe | Método | Linhas | CC |");
        sb.AppendLine("|---------|--------|--------|--------|----|");
        foreach (var x in methods.OrderByDescending(x => x.Method.LinesOfCode).Take(10))
            sb.AppendLine($"| {x.File} | {x.Type} | {x.Method.Name} | {x.Method.LinesOfCode} | {x.Method.CyclomaticComplexity} |");
        sb.AppendLine();
        sb.AppendLine("## 📊 Distribuição de Complexidade");
        sb.AppendLine("| Faixa | Qtd | % |"); sb.AppendLine("|-------|-----|---|");
        foreach (var (min, max, label) in new[] { (1, 5, "Baixa (1-5)"), (6, 10, "Média (6-10)"), (11, 20, "Alta (11-20)"), (21, int.MaxValue, "Crítica (21+)") })
        {
            var c = methods.Count(x => x.Method.CyclomaticComplexity >= min && x.Method.CyclomaticComplexity <= max);
            sb.AppendLine($"| {label} | {c} | {(methods.Count > 0 ? c * 100 / methods.Count : 0)}% |");
        }

        await File.WriteAllTextAsync(Path.Combine(outputPath, "oracle_metrics.md"), sb.ToString());
        logger.LogInformation("✔ oracle_metrics.md criado");
    }

    // ── Code Smells Report ────────────────────────────────────────

    static async Task GenerateSmellsReportAsync(ProjectOracle project, string outputPath, ILogger logger)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Oracle Pack — Code Smells");
        sb.AppendLine($"> {project.GeneratedAt:dd/MM/yyyy HH:mm:ss}  |  Total: {project.CodeSmells.Count}");
        sb.AppendLine();
        foreach (var group in project.CodeSmells.GroupBy(s => s.Severity).OrderByDescending(g => g.Key))
        {
            var icon = group.Key switch { Severity.Error => "🔴", Severity.Warning => "🟡", _ => "🔵" };
            sb.AppendLine($"## {icon} {group.Key} ({group.Count()})");
            sb.AppendLine("| Arquivo | Classe | Método | Problema | Detalhe |");
            sb.AppendLine("|---------|--------|--------|----------|---------|");
            foreach (var s in group)
                sb.AppendLine($"| {s.FileName} | {s.TypeName} | {s.MethodName ?? "-"} | {s.Kind} | {s.Detail} |");
            sb.AppendLine();
        }
        await File.WriteAllTextAsync(Path.Combine(outputPath, "oracle_smells.md"), sb.ToString());
        logger.LogInformation("✔ oracle_smells.md criado ({Count} smells)", project.CodeSmells.Count);
    }

    // ── HTML interativo ───────────────────────────────────────────

    static async Task GenerateHtmlAsync(ProjectOracle project, AppConfig config, ILogger logger)
    {
        var smellHighCyclo = config.SmellHighCyclomaticScore;
        var sb = new StringBuilder();
        sb.AppendLine("""
        <!DOCTYPE html><html lang="pt-BR"><head>
        <meta charset="UTF-8"/><meta name="viewport" content="width=device-width,initial-scale=1"/>
        <title>Oracle Pack</title>
        <style>
        :root{--bg:#0f172a;--card:#1e293b;--border:#334155;--text:#e2e8f0;--muted:#94a3b8;--accent:#38bdf8;--warn:#fbbf24;--err:#f87171;--ok:#4ade80}
        *{box-sizing:border-box;margin:0;padding:0}body{font-family:'Segoe UI',sans-serif;background:var(--bg);color:var(--text);padding:24px}
        h1{color:var(--accent);margin-bottom:4px}.meta{color:var(--muted);font-size:.85rem;margin-bottom:20px}
        .stats{display:flex;gap:12px;flex-wrap:wrap;margin-bottom:24px}
        .stat{background:var(--card);border:1px solid var(--border);border-radius:8px;padding:12px 20px;min-width:120px}
        .stat-n{font-size:1.8rem;font-weight:700;color:var(--accent)}.stat-l{font-size:.8rem;color:var(--muted)}
        .health-bar{width:100%;height:8px;background:var(--border);border-radius:4px;margin-bottom:24px}
        .health-fill{height:8px;border-radius:4px;transition:width .5s}
        .tabs{display:flex;gap:8px;margin-bottom:20px}
        .tab{padding:8px 16px;background:var(--card);border:1px solid var(--border);border-radius:6px;cursor:pointer;font-size:.85rem}
        .tab.active{border-color:var(--accent);color:var(--accent)}
        .tab-content{display:none}.tab-content.active{display:block}
        .search{width:100%;padding:10px 14px;background:var(--card);border:1px solid var(--border);border-radius:8px;color:var(--text);font-size:1rem;margin-bottom:20px}
        .search:focus{outline:none;border-color:var(--accent)}
        .ns-group{margin-bottom:8px}
        .ns-header{background:var(--card);border:1px solid var(--border);border-radius:8px;padding:10px 16px;cursor:pointer;display:flex;justify-content:space-between;align-items:center}
        .ns-header:hover{border-color:var(--accent)}.ns-label{font-weight:600;color:var(--accent)}
        .ns-body{display:none;padding:8px 0 0}.ns-body.open{display:block}
        .type-card{background:var(--card);border:1px solid var(--border);border-radius:8px;margin-bottom:8px;overflow:hidden}
        .type-header{padding:10px 16px;cursor:pointer;display:flex;justify-content:space-between;align-items:center}
        .type-header:hover{background:#263350}
        .type-body{display:none;padding:12px 16px;border-top:1px solid var(--border)}.type-body.open{display:block}
        .sec{font-size:.75rem;text-transform:uppercase;color:var(--muted);margin:12px 0 6px;letter-spacing:.05em}
        .method{padding:6px 0;border-bottom:1px solid var(--border);font-size:.85rem}.method:last-child{border-bottom:none}
        .sig{font-family:monospace}.mmeta{font-size:.75rem;color:var(--muted);margin-top:2px}
        .cc-ok{color:var(--ok)}.cc-warn{color:var(--warn)}.cc-err{color:var(--err)}
        .risk-h{color:var(--err)}.risk-m{color:var(--warn)}.risk-l{color:var(--ok)}
        .badge{font-size:.7rem;padding:2px 6px;border-radius:4px;margin-left:4px}
        .b-async{background:#1d4ed8;color:#bfdbfe}.b-smell{background:#7f1d1d;color:#fca5a5}
        .b-risk-h{background:#450a0a;color:#fca5a5}.b-risk-m{background:#451a03;color:#fed7aa}.b-dead{background:#1e1b4b;color:#c7d2fe}
        .summary{font-style:italic;color:var(--muted);font-size:.8rem;margin-top:2px}
        .risk-row,.smell-row,.dead-row{padding:6px 0;border-bottom:1px solid var(--border);font-size:.83rem}
        .s-err{color:var(--err)}.s-warn{color:var(--warn)}.s-info{color:var(--accent)}
        .hidden{display:none!important}
        .score-bar{display:inline-block;width:80px;height:8px;background:var(--border);border-radius:4px;vertical-align:middle;margin-left:6px}
        .score-fill{height:8px;border-radius:4px}
        </style></head><body>
        """);

        var health = project.Intelligence?.ProjectHealthScore ?? 0;
        var healthColor = health >= 80 ? "#4ade80" : health >= 50 ? "#fbbf24" : "#f87171";
        var arch = project.Intelligence?.ArchFingerprint.Pattern ?? "?";

        sb.AppendLine($"<h1>🔮 Oracle Pack</h1>");
        sb.AppendLine($"<p class='meta'>Gerado em {project.GeneratedAt:dd/MM/yyyy HH:mm:ss} &nbsp;|&nbsp; {H(project.RootPath)} &nbsp;|&nbsp; Arquitetura: <b>{H(arch)}</b></p>");
        sb.AppendLine($"<div class='health-bar'><div class='health-fill' style='width:{health}%;background:{healthColor}'></div></div>");

        sb.AppendLine("<div class='stats'>");
        void Stat(string n, string l) => sb.AppendLine($"<div class='stat'><div class='stat-n'>{n}</div><div class='stat-l'>{l}</div></div>");
        Stat($"{health}/100", "💚 Saúde");
        Stat($"{project.TotalFiles}", "Arquivos");
        Stat($"{project.TotalTypes}", "Tipos");
        Stat($"{project.TotalMethods}", "Métodos");
        Stat($"{project.CodeSmells.Count(s => s.Severity == Severity.Error)}", "🔴 Erros");
        Stat($"{project.Intelligence?.RiskMap.Count(r => r.Level == RiskLevel.High) ?? 0}", "⚠️ Alto Risco");
        Stat($"{project.Intelligence?.DeadCode.Count(d => d.Confidence == DeadCodeConfidence.High) ?? 0}", "🪦 Cód. Morto");
        sb.AppendLine("</div>");

        // Tabs
        sb.AppendLine("""
        <div class='tabs'>
          <div class='tab active' onclick='switchTab("types",this)'>🧩 Tipos</div>
          <div class='tab' onclick='switchTab("risk",this)'>⚠️ Risco</div>
          <div class='tab' onclick='switchTab("dead",this)'>🪦 Cód. Morto</div>
          <div class='tab' onclick='switchTab("smells",this)'>👃 Smells</div>
          <div class='tab' onclick='switchTab("arch",this)'>🏛️ Arquitetura</div>
          <div class='tab' onclick='switchTab("intel",this)'>🧠 Intelligence</div>
        </div>
        """);

        // ── Tab: Tipos ────────────────────────────────────────────
        sb.AppendLine("<div id='tab-types' class='tab-content active'>");
        sb.AppendLine("<input class='search' id='search' placeholder='🔍 Buscar tipo, método, namespace...' oninput='filterAll(this.value)'/>");

        var byNamespace = project.Files.SelectMany(f => f.Types.Select(t => (f, t))).GroupBy(x => x.t.Namespace).OrderBy(g => g.Key);
        foreach (var ns in byNamespace)
        {
            sb.AppendLine($"<div class='ns-group' data-ns='{H(ns.Key)}'>");
            sb.AppendLine($"<div class='ns-header' onclick='toggleNs(this)'><span class='ns-label'>📦 {H(ns.Key)}</span><span style='color:var(--muted);font-size:.8rem'>{ns.Count()} tipos ▾</span></div>");
            sb.AppendLine("<div class='ns-body'>");
            foreach (var (file, type) in ns)
            {
                var hasSmell = project.CodeSmells.Any(s => s.TypeName == type.Name && s.FileName == file.FileName);
                var topRisk = project.Intelligence?.RiskMap.Where(r => r.TypeName == type.Name).OrderByDescending(r => r.Score).FirstOrDefault();
                var riskBadge = topRisk?.Level switch { RiskLevel.High => "<span class='badge b-risk-h'>🔴 alto risco</span>", RiskLevel.Medium => "<span class='badge b-risk-m'>🟡 médio risco</span>", _ => "" };

                sb.AppendLine($"<div class='type-card' data-name='{H(type.Name)}' data-file='{H(file.FileName)}'>");
                sb.AppendLine($"<div class='type-header' onclick='toggleBody(this)'><span><span style='color:var(--muted);font-size:.75rem'>{type.Kind} </span><b>{H(type.Name)}</b>{(hasSmell ? "<span class='badge b-smell'>⚠ smell</span>" : "")}{riskBadge}</span><span style='color:var(--muted);font-size:.8rem'>{type.Methods.Count}m {type.Properties.Count}p ▾</span></div>");
                sb.AppendLine("<div class='type-body'>");
                if (!string.IsNullOrWhiteSpace(type.XmlSummary)) sb.AppendLine($"<div class='summary'>📝 {H(type.XmlSummary)}</div>");
                if (type.BaseTypes.Any()) sb.AppendLine($"<div class='mmeta'>Herda/Implementa: {H(string.Join(", ", type.BaseTypes))}</div>");

                if (type.Properties.Any()) { sb.AppendLine("<div class='sec'>Propriedades</div>"); foreach (var p in type.Properties) sb.AppendLine($"<div class='method'><span class='sig'>{H(p.Type)} {H(p.Name)}</span></div>"); }

                if (type.Methods.Any())
                {
                    sb.AppendLine("<div class='sec'>Métodos</div>");
                    foreach (var m in type.Methods)
                    {
                        var risk = project.Intelligence?.RiskMap.FirstOrDefault(r => r.TypeName == type.Name && r.MethodName == m.Name);
                        var ccClass = m.CyclomaticComplexity >= smellHighCyclo ? "cc-err" : m.CyclomaticComplexity >= 6 ? "cc-warn" : "cc-ok";
                        var riskCls = risk?.Level switch { RiskLevel.High => "risk-h", RiskLevel.Medium => "risk-m", _ => "risk-l" };
                        var riskTxt = risk != null ? $"<span class='{riskCls}' title='Risk Score: {risk.Score}'>● </span>" : "";
                        var parms = string.Join(", ", m.Parameters.Select(p => $"{p.Type} {p.Name}"));
                        sb.AppendLine("<div class='method'>");
                        sb.AppendLine($"<div class='sig'>{riskTxt}{H(m.ReturnType)} {H(m.Name)}({H(parms)}){(m.IsAsync ? "<span class='badge b-async'>async</span>" : "")}</div>");
                        sb.AppendLine($"<div class='mmeta'>{H(m.Modifiers)} | {m.LinesOfCode}L | <span class='{ccClass}'>CC={m.CyclomaticComplexity}</span>{(risk != null ? $" | Score {risk.Score}/100" : "")}</div>");
                        if (!string.IsNullOrWhiteSpace(m.XmlSummary)) sb.AppendLine($"<div class='summary'>📝 {H(m.XmlSummary)}</div>");
                        sb.AppendLine("</div>");
                    }
                }
                sb.AppendLine("</div></div>");
            }
            sb.AppendLine("</div></div>");
        }
        sb.AppendLine("</div>"); // tab-types

        // ── Tab: Risco ────────────────────────────────────────────
        sb.AppendLine("<div id='tab-risk' class='tab-content'>");
        if (project.Intelligence?.RiskMap.Any() == true)
        {
            foreach (var level in new[] { RiskLevel.High, RiskLevel.Medium, RiskLevel.Low })
            {
                var entries = project.Intelligence.RiskMap.Where(r => r.Level == level).ToList();
                if (!entries.Any()) continue;
                var icon = level == RiskLevel.High ? "🔴" : level == RiskLevel.Medium ? "🟡" : "🟢";
                sb.AppendLine($"<h3 style='color:var(--muted);margin:16px 0 8px'>{icon} Risco {level} — {entries.Count} método(s)</h3>");
                foreach (var r in entries.Take(30))
                {
                    var scoreColor = r.Score >= 70 ? "#f87171" : r.Score >= 40 ? "#fbbf24" : "#4ade80";
                    sb.AppendLine($"<div class='risk-row'><b>{H(r.TypeName)}.{H(r.MethodName)}()</b> <span style='color:var(--muted)'>{H(r.FileName)}</span>");
                    sb.AppendLine($"<div class='mmeta'>Score: <b style='color:{scoreColor}'>{r.Score}/100</b> &nbsp;|&nbsp; CC={r.CyclomaticComplexity} &nbsp;|&nbsp; {r.LinesOfCode}L &nbsp;|&nbsp; {r.Dependents} dependentes{(r.HasSmell ? " &nbsp;|&nbsp; ⚠️ smell" : "")}{(r.NoDoc ? " &nbsp;|&nbsp; 📝 sem doc" : "")}</div></div>");
                }
            }
        }
        else sb.AppendLine("<p style='color:var(--muted);padding:20px'>Nenhum dado de risco disponível.</p>");
        sb.AppendLine("</div>");

        // ── Tab: Código Morto ─────────────────────────────────────
        sb.AppendLine("<div id='tab-dead' class='tab-content'>");
        var deadHigh = project.Intelligence?.DeadCode.Where(d => d.Confidence == DeadCodeConfidence.High).ToList() ?? [];
        var deadLow = project.Intelligence?.DeadCode.Where(d => d.Confidence == DeadCodeConfidence.Low).ToList() ?? [];
        sb.AppendLine("<p style='color:var(--muted);font-size:.85rem;margin-bottom:16px'>⚠️ Verifique manualmente. Reflection, eventos e APIs externas não são detectados.</p>");
        if (deadHigh.Any())
        {
            sb.AppendLine($"<h3 style='color:var(--muted);margin-bottom:8px'>🪦 Alta confiança — privados nunca chamados ({deadHigh.Count})</h3>");
            foreach (var d in deadHigh) sb.AppendLine($"<div class='dead-row'><b>{H(d.TypeName)}.{H(d.MethodName)}()</b> <span style='color:var(--muted)'>{H(d.FileName)}</span> <span class='badge b-dead'>remover com segurança</span></div>");
        }
        if (deadLow.Any())
        {
            sb.AppendLine($"<h3 style='color:var(--muted);margin:16px 0 8px'>🤔 Baixa confiança — públicos sem callers internos ({deadLow.Count})</h3>");
            foreach (var d in deadLow.Take(20)) sb.AppendLine($"<div class='dead-row'><b>{H(d.TypeName)}.{H(d.MethodName)}()</b> <span style='color:var(--muted)'>{H(d.FileName)}</span></div>");
        }
        if (!deadHigh.Any() && !deadLow.Any()) sb.AppendLine("<p style='color:var(--muted);padding:20px'>Nenhum código morto detectado.</p>");
        sb.AppendLine("</div>");

        // ── Tab: Smells ───────────────────────────────────────────
        sb.AppendLine("<div id='tab-smells' class='tab-content'>");
        foreach (var group in project.CodeSmells.GroupBy(s => s.Severity).OrderByDescending(g => g.Key))
        {
            var cls = group.Key switch { Severity.Error => "s-err", Severity.Warning => "s-warn", _ => "s-info" };
            var ico = group.Key switch { Severity.Error => "🔴", Severity.Warning => "🟡", _ => "🔵" };
            sb.AppendLine($"<h3 style='color:var(--muted);margin:16px 0 8px'>{ico} {group.Key} ({group.Count()})</h3>");
            foreach (var s in group)
                sb.AppendLine($"<div class='smell-row'><span class='{cls}'>[{s.Severity}]</span> <b>{H(s.FileName)}</b> › {H(s.TypeName)}{(s.MethodName != null ? $" › {H(s.MethodName)}" : "")} — {H(s.Kind)}: {H(s.Detail)}</div>");
        }
        sb.AppendLine("</div>");

        // ── Tab: Arquitetura ──────────────────────────────────────
        sb.AppendLine("<div id='tab-arch' class='tab-content'>");
        var af = project.Intelligence?.ArchFingerprint;
        if (af != null)
        {
            sb.AppendLine($"<h2 style='color:var(--accent);margin-bottom:16px'>🏛️ {H(af.Pattern)}</h2>");
            sb.AppendLine($"<p style='color:var(--muted);margin-bottom:16px'>Interfaces: {af.InterfaceCount} &nbsp;|&nbsp; Classes concretas: {af.ConcreteCount}</p>");
            if (af.DetectedPatterns.Any()) { sb.AppendLine("<h3 style='color:var(--muted);margin-bottom:8px'>✅ Padrões detectados</h3>"); foreach (var p in af.DetectedPatterns) sb.AppendLine($"<div class='smell-row s-info'>• {H(p)}</div>"); }
            if (af.ArchitectureViolations.Any()) { sb.AppendLine("<h3 style='color:var(--muted);margin:16px 0 8px'>❌ Violações</h3>"); foreach (var v in af.ArchitectureViolations) sb.AppendLine($"<div class='smell-row s-err'>• {H(v)}</div>"); }
        }
        sb.AppendLine("</div>");

        // ── Tab: Intelligence ─────────────────────────────────────
        sb.AppendLine("<div id='tab-intel' class='tab-content'>");
        var intel = project.Intelligence;
        if (intel != null)
        {
            if (intel.Duplicates.Any()) { sb.AppendLine($"<h3 style='color:var(--muted);margin-bottom:8px'>🔁 Duplicação Estrutural ({intel.Duplicates.Count})</h3>"); foreach (var d in intel.Duplicates.Take(10)) sb.AppendLine($"<div class='smell-row'><b>{H(d.TypeNameA)}.{H(d.MethodNameA)}</b> ≈ <b>{H(d.TypeNameB)}.{H(d.MethodNameB)}</b> — {d.SimilarityPercent}% similar</div>"); sb.AppendLine("<br/>"); }
            if (intel.ExtractionSuggestions.Any()) { sb.AppendLine($"<h3 style='color:var(--muted);margin-bottom:8px'>💡 Sugestões de Extração ({intel.ExtractionSuggestions.Count})</h3>"); foreach (var e in intel.ExtractionSuggestions.Take(8)) sb.AppendLine($"<div class='smell-row'><b>{H(e.TypeName)}.{H(e.MethodName)}()</b> CC={e.CurrentCC}, {e.CurrentLines}L → dividir em {e.SuggestedClusters.Count} métodos</div>"); sb.AppendLine("<br/>"); }
            if (intel.TemporalCoupling.Any()) { sb.AppendLine($"<h3 style='color:var(--muted);margin-bottom:8px'>🔗 Acoplamento Temporal ({intel.TemporalCoupling.Count})</h3>"); foreach (var t in intel.TemporalCoupling.Take(8)) sb.AppendLine($"<div class='smell-row'>{H(t.CallA)} → {H(t.CallB)} <span style='color:var(--muted)'>({t.OccurrenceCount}x)</span></div>"); }
        }
        sb.AppendLine("</div>");

        sb.AppendLine("""
        <script>
        function toggleNs(el){el.nextElementSibling.classList.toggle('open')}
        function toggleBody(el){el.nextElementSibling.classList.toggle('open')}
        function filterAll(q){
          q=q.toLowerCase();
          document.querySelectorAll('.ns-group').forEach(ns=>{
            let any=false;
            ns.querySelectorAll('.type-card').forEach(card=>{
              const ok=!q||card.dataset.name?.toLowerCase().includes(q)||card.dataset.file?.toLowerCase().includes(q)||card.textContent.toLowerCase().includes(q);
              card.classList.toggle('hidden',!ok);if(ok){any=true;}
            });
            ns.classList.toggle('hidden',!any);
            if(q&&any)ns.querySelector('.ns-body').classList.add('open');
          });
        }
        function switchTab(id,el){
          document.querySelectorAll('.tab-content').forEach(t=>t.classList.remove('active'));
          document.querySelectorAll('.tab').forEach(t=>t.classList.remove('active'));
          document.getElementById('tab-'+id).classList.add('active');
          el.classList.add('active');
        }
        </script></body></html>
        """);

        await File.WriteAllTextAsync(Path.Combine(config.OutputPath, "oracle.html"), sb.ToString());
        logger.LogInformation("✔ oracle.html criado (com 6 tabs)");
    }

    static string H(string? s) => (s ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}

// ══════════════════════════════════════════════════════════════
//  MODELS
// ══════════════════════════════════════════════════════════════

record AppConfig(
    string ProjectPath, string OutputPath,
    HashSet<string> IgnoredDirectories, HashSet<string> IgnoredFileSuffixes,
    int MaxParallel, int MaxFileSizeKb, int MaxDepth, int LlmMaxChars,
    int SmellGodClassMethods, int SmellLongMethodLines, int SmellHighCyclomaticScore,
    int RiskHighDependents);

enum OutputMode { Fast, Full, Metrics }
enum Severity { Info, Warning, Error }
enum RiskLevel { Low, Medium, High }
enum DeadCodeConfidence { Low, High }

class SolutionOracle
{
    public string RootPath { get; set; } = "";
    public DateTime GeneratedAt { get; set; }
    public int TotalProjects { get; set; }
    public List<ProjectOracle> Projects { get; set; } = [];
    public Dictionary<string, List<string>> DependencyMap { get; set; } = [];
}

class ProjectOracle
{
    public string RootPath { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public DateTime GeneratedAt { get; set; }
    public int TotalFiles { get; set; }
    public int TotalTypes { get; set; }
    public int TotalMethods { get; set; }
    public int TotalProperties { get; set; }
    public List<FileOracle> Files { get; set; } = [];
    public List<CodeSmell> CodeSmells { get; set; } = [];
    public ProjectIntelligence? Intelligence { get; set; }
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
    public int CyclomaticComplexity { get; set; }
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

class FieldOracle { public List<string> Names { get; set; } = []; public string Type { get; set; } = ""; public string Modifiers { get; set; } = ""; }
class ConstructorOracle { public string Modifiers { get; set; } = ""; public string? XmlSummary { get; set; } public List<ParameterOracle> Parameters { get; set; } = []; }
class ParameterOracle { public string Name { get; set; } = ""; public string Type { get; set; } = ""; public string? Default { get; set; } }

record CodeSmell(string FileName, string TypeName, string Kind, string Detail, Severity Severity, string? MethodName = null);

class CacheEntry
{
    public string Hash { get; set; } = ""; public FileOracle FileOracle { get; set; } = new();
    public CacheEntry() { }
    public CacheEntry(string h, FileOracle fo) { Hash = h; FileOracle = fo; }
}

// ── Intelligence Models ───────────────────────────────────────

class ProjectIntelligence
{
    public List<RiskEntry> RiskMap { get; set; } = [];
    public List<DeadCodeEntry> DeadCode { get; set; } = [];
    public Dictionary<string, ImpactNode> ImpactGraph { get; set; } = [];
    public List<DuplicateEntry> Duplicates { get; set; } = [];
    public List<ExtractionSuggestion> ExtractionSuggestions { get; set; } = [];
    public List<TemporalCouplingEntry> TemporalCoupling { get; set; } = [];
    public ArchitectureFingerprint ArchFingerprint { get; set; } = new("?", [], [], 0, 0);
    public int ProjectHealthScore { get; set; }
    public List<TestSuggestion> TestSuggestions { get; set; } = [];
}

record RiskEntry(string FileName, string TypeName, string MethodName,
    int Score, RiskLevel Level, int Dependents, int CyclomaticComplexity, int LinesOfCode, bool NoDoc, bool HasSmell);

record DeadCodeEntry(string FileName, string TypeName, string MethodName, DeadCodeConfidence Confidence);

class ImpactNode
{
    public string FileName { get; set; }
    public string TypeName { get; set; }
    public string MethodName { get; set; }
    public List<string> Calls { get; set; } = [];
    public List<string> CalledBy { get; set; } = [];
    public ImpactNode(string f, string t, string m) { FileName = f; TypeName = t; MethodName = m; }
}

record DuplicateEntry(string FileNameA, string TypeNameA, string MethodNameA,
    string FileNameB, string TypeNameB, string MethodNameB, int SimilarityPercent);

record ExtractionSuggestion(string FileName, string TypeName, string MethodName,
    int CurrentCC, int CurrentLines, List<List<string>> SuggestedClusters);

record TemporalCouplingEntry(string CallA, string CallB, int OccurrenceCount);

record ArchitectureFingerprint(string Pattern, List<string> DetectedPatterns,
    List<string> ArchitectureViolations, int InterfaceCount, int ConcreteCount);

record TestSuggestion(string FileName, string TypeName, string MethodName, int CC, List<string> SuggestedCases);
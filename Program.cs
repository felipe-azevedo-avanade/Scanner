using System.Configuration;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

class Program
{
    static void Main()
    {
        try
        {
            var projectPath = ConfigurationManager.AppSettings["ProjectPath"];
            var ignoredDirsRaw = ConfigurationManager.AppSettings["IgnoredDirectories"];
            var outputPath = ConfigurationManager.AppSettings["LogOutputPath"];

            if (string.IsNullOrWhiteSpace(projectPath))
                throw new Exception("App.config não contém ProjectPath.");

            if (string.IsNullOrWhiteSpace(outputPath))
                throw new Exception("App.config não contém LogOutputPath.");

            var ignoredDirs = (ignoredDirsRaw ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .ToList();

            Console.WriteLine($"ProjectPath: {projectPath}");
            Console.WriteLine($"OutputPath : {outputPath}");
            Console.WriteLine();

            if (!Directory.Exists(projectPath))
                throw new DirectoryNotFoundException($"Projeto não encontrado: {projectPath}");

            Directory.CreateDirectory(outputPath);

            Console.WriteLine("Iniciando scan...");

            var csFiles = Directory
                .GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
                .Where(f => !ignoredDirs.Any(d => f.Contains($@"\{d}\", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Console.WriteLine($"Arquivos C#: {csFiles.Count}");
            Console.WriteLine();

            var project = new ProjectOracle
            {
                RootPath = projectPath,
                Files = new List<FileOracle>()
            };

            foreach (var file in csFiles)
            {
                Console.WriteLine($"Lendo: {Path.GetFileName(file)}");

                var code = File.ReadAllText(file);
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = tree.GetCompilationUnitRoot();

                var fileOracle = new FileOracle
                {
                    FileName = Path.GetFileName(file),
                    Types = new List<TypeOracle>()
                };

                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var typeOracle = new TypeOracle
                    {
                        Name = typeDecl.Identifier.Text,
                        Kind = typeDecl.Keyword.Text,
                        Methods = new List<MethodOracle>()
                    };

                    foreach (var method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
                    {
                        var methodOracle = new MethodOracle
                        {
                            Name = method.Identifier.Text,
                            ReturnType = method.ReturnType.ToString(),
                            Calls = method
                                .DescendantNodes()
                                .OfType<InvocationExpressionSyntax>()
                                .Select(i => i.Expression.ToString())
                                .Distinct()
                                .ToList()
                        };

                        typeOracle.Methods.Add(methodOracle);
                    }

                    fileOracle.Types.Add(typeOracle);
                }

                if (fileOracle.Types.Any())
                    project.Files.Add(fileOracle);
            }

            project.TotalFiles = project.Files.Count;
            project.TotalTypes = project.Files.Sum(f => f.Types.Count);
            project.TotalMethods = project.Files.Sum(f => f.Types.Sum(t => t.Methods.Count));

            GenerateJson(project, outputPath);
            GenerateMarkdown(project, outputPath);

            Console.WriteLine();
            Console.WriteLine("✔ Oracle Pack gerado com sucesso.");
            Console.WriteLine($"Arquivos salvos em: {outputPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("ERRO FATAL:");
            Console.WriteLine(ex.Message);
        }

        Console.WriteLine();
        Console.WriteLine("Pressione ENTER para sair...");
        Console.ReadLine();
    }

    static void GenerateJson(ProjectOracle project, string outputPath)
    {
        var json = JsonSerializer.Serialize(project,
            new JsonSerializerOptions { WriteIndented = true });

        var file = Path.Combine(outputPath, "oracle.json");
        File.WriteAllText(file, json);

        Console.WriteLine($"✔ oracle.json criado");
    }

    static void GenerateMarkdown(ProjectOracle project, string outputPath)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Oracle Pack");
        sb.AppendLine();
        sb.AppendLine($"Root: {project.RootPath}");
        sb.AppendLine();
        sb.AppendLine("## Estatísticas");
        sb.AppendLine($"- Arquivos: {project.TotalFiles}");
        sb.AppendLine($"- Tipos: {project.TotalTypes}");
        sb.AppendLine($"- Métodos: {project.TotalMethods}");
        sb.AppendLine();

        foreach (var file in project.Files)
        {
            sb.AppendLine($"## {file.FileName}");

            foreach (var type in file.Types)
            {
                sb.AppendLine($"### {type.Kind} {type.Name}");

                foreach (var method in type.Methods)
                {
                    sb.AppendLine($"- {method.ReturnType} {method.Name}()");

                    foreach (var call in method.Calls)
                        sb.AppendLine($"  - calls: {call}");
                }
            }

            sb.AppendLine();
        }

        var filePath = Path.Combine(outputPath, "oracle.md");
        File.WriteAllText(filePath, sb.ToString());

        Console.WriteLine($"✔ oracle.md criado");
    }
}

#region Models

class ProjectOracle
{
    public string RootPath { get; set; }
    public int TotalFiles { get; set; }
    public int TotalTypes { get; set; }
    public int TotalMethods { get; set; }
    public List<FileOracle> Files { get; set; }
}

class FileOracle
{
    public string FileName { get; set; }
    public List<TypeOracle> Types { get; set; }
}

class TypeOracle
{
    public string Name { get; set; }
    public string Kind { get; set; }
    public List<MethodOracle> Methods { get; set; }
}

class MethodOracle
{
    public string Name { get; set; }
    public string ReturnType { get; set; }
    public List<string> Calls { get; set; }
}

#endregion
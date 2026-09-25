using System.Collections.Immutable;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Core.Inspection;

/// <summary>
/// Extracts verifiable declarations from an authorized local .NET repository without executing code.
/// Evidence categories ending in .candidate are observations, not proven runtime communications.
/// </summary>
public static class RepositoryFactExtractor
{
    private const int MaxEvidence = 10_000;
    private const int MaxEvidencePerFile = 256;

    private static readonly string[] IntegrationNames = ["postgresql", "rabbitmq", "redis"];

    private static readonly string[] HttpSignals =
    [
        "WebApplication.CreateBuilder(",
        "WebApplication.Run(",
        "MapGet(",
        "MapPost(",
        "MapControllers(",
        "AddControllers(",
    ];

    private static readonly string[] WorkerSignals =
    [
        "AddHostedService<",
        "BackgroundService",
        "IHostedService",
        "Host.CreateApplicationBuilder(",
    ];

    /// <summary>
    /// Performs a bounded inventory first, then opens only accepted files with renewed containment
    /// checks. No AI, process execution, networking, project evaluation, or runtime graph generation.
    /// </summary>
    public static RepositorySnapshot Extract(
        RepositoryScanOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        RepositorySnapshot snapshot = RepositoryScanner.Scan(options, cancellationToken);
        string root = Path.GetFullPath(options.RootPath);
        HashSet<string> inventoried = snapshot.Files
            .Select(file => file.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        List<Evidence> evidence = [];
        List<RepositoryDiagnostic> diagnostics = [.. snapshot.Diagnostics];

        foreach (RepositoryFile file in snapshot.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (evidence.Count >= MaxEvidence)
            {
                diagnostics.Add(new RepositoryDiagnostic(
                    "extract.evidenceLimit",
                    DiagnosticSeverity.Warning,
                    null,
                    "Evidence budget reached; remaining files were not examined."));
                break;
            }

            string relative = file.RelativePath;
            string name = Path.GetFileName(relative);
            string extension = Path.GetExtension(name);
            bool isProject = extension is ".csproj" or ".fsproj";
            bool isSolution = extension is ".sln" or ".slnx";
            bool isSource = extension is ".cs" or ".fs";
            bool isContainerManifest = name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)
                || name.Equals("compose.yaml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("compose.yml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("docker-compose.yaml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("docker-compose.yml", StringComparison.OrdinalIgnoreCase);

            if (!isProject && !isSolution && !isSource && !isContainerManifest)
            {
                continue;
            }

            // A manifest filename proves the file is present, not that the image is deployed.
            if (isContainerManifest)
            {
                Add(evidence, "deployment.docker.manifest", relative, null,
                    EvidenceSourceType.Manifest, "Docker-related manifest is present.", cancellationToken);
                continue;
            }

            (string? content, string? error) = RepositoryFileReader.Read(root, file, options, cancellationToken);
            if (error is not null)
            {
                diagnostics.Add(new RepositoryDiagnostic(error, DiagnosticSeverity.Warning,
                    relative, "Inventoried file was not read; no facts were inferred from it."));
                continue;
            }

            if (isProject || extension == ".slnx")
            {
                ExtractXml(content!, relative, root, isProject, inventoried, evidence, diagnostics, cancellationToken);
            }
            else if (isSolution)
            {
                ExtractLegacySolution(content!, relative, root, inventoried, evidence, diagnostics, cancellationToken);
            }
            else
            {
                ExtractSourceSignals(content!, relative, evidence, diagnostics, cancellationToken);
            }
        }

        RepositorySnapshot result = snapshot with
        {
            Evidence = [.. evidence.OrderBy(item => item.Id, StringComparer.Ordinal)],
            Diagnostics =
            [
                .. diagnostics
                    .OrderBy(item => item.Code, StringComparer.Ordinal)
                    .ThenBy(item => item.RelativePath, StringComparer.Ordinal)
                    .ThenBy(item => item.Message, StringComparer.Ordinal),
            ],
        };
        ImmutableArray<ContractError> errors = ContractValidator.ValidateSnapshot(result);
        if (!errors.IsEmpty)
        {
            throw new ContractValidationException(errors);
        }

        return result;
    }

    private static void ExtractXml(
        string content,
        string relative,
        string root,
        bool isProject,
        HashSet<string> inventoried,
        List<Evidence> evidence,
        List<RepositoryDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        XDocument document;
        try
        {
            XmlReaderSettings settings = new()
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = RepositoryFileReader.MaxExtractedFileBytes,
                MaxCharactersFromEntities = 0,
            };
            using StringReader source = new(content);
            using XmlReader reader = XmlReader.Create(source, settings);
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            diagnostics.Add(new RepositoryDiagnostic("extract.invalidXml", DiagnosticSeverity.Warning,
                relative, "Invalid or unsafe XML; no XML facts were extracted."));
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (document.Root is null || document.Root.Name.LocalName != (isProject ? "Project" : "Solution"))
        {
            diagnostics.Add(new RepositoryDiagnostic("extract.invalidRoot", DiagnosticSeverity.Warning,
                relative, "XML root does not match the expected .NET file type."));
            return;
        }

        if (isProject)
        {
            ExtractProject(document, relative, root, inventoried, evidence, diagnostics, cancellationToken);
        }
        else
        {
            foreach (XElement project in document.Descendants().Where(element => element.Name.LocalName == "Project"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? include = project.Attribute("Path")?.Value;
                RecordReference(include, relative, root, inventoried, evidence, diagnostics,
                    "dotnet.solution.project", "Solution declares an inventoried project: ", Line(project), cancellationToken);
            }
        }
    }

    private static void ExtractProject(
        XDocument document,
        string relative,
        string root,
        HashSet<string> inventoried,
        List<Evidence> evidence,
        List<RepositoryDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        XElement project = document.Root!;
        Add(evidence, "dotnet.project", relative, Line(project),
            EvidenceSourceType.ProjectFile, "MSBuild project manifest is present.", cancellationToken);

        string? sdk = project.Attribute("Sdk")?.Value;
        bool webSdk = sdk is not null && sdk.Split(';').Any(item => item.Trim() == "Microsoft.NET.Sdk.Web");
        bool workerSdk = sdk is not null && sdk.Split(';').Any(item => item.Trim() == "Microsoft.NET.Sdk.Worker");
        IEnumerable<XElement> nodes = project.Descendants();
        bool test = nodes.Any(element => element.Name.LocalName == "IsTestProject"
            && element.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
        XElement? outputType = nodes.FirstOrDefault(element => element.Name.LocalName == "OutputType");
        string type = test ? "Test" : webSdk || workerSdk || outputType?.Value.Trim() is "Exe" or "WinExe"
            ? "Executable" : "Library";
        Add(evidence, "dotnet.project.kind", relative, Line(outputType ?? project),
            EvidenceSourceType.ProjectFile, "Project declaration indicates kind: " + type + ".", cancellationToken);

        if (webSdk)
        {
            Add(evidence, "dotnet.runtime.http.candidate", relative, Line(project),
                EvidenceSourceType.ProjectFile, "Web SDK declaration is an HTTP-host candidate, not proof of a running endpoint.", cancellationToken);
        }

        if (workerSdk)
        {
            Add(evidence, "dotnet.runtime.worker.candidate", relative, Line(project),
                EvidenceSourceType.ProjectFile, "Worker SDK declaration is a worker-host candidate, not proof of execution.", cancellationToken);
        }

        foreach (XElement node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            {
                foreach (string framework in node.Value.Split(';'))
                {
                    string normalized = framework.Trim();
                    if (Regex.IsMatch(normalized, @"^net(?:standard|coreapp)?[0-9]+(?:\.[0-9]+)*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                    {
                        Add(evidence, "dotnet.project.targetFramework", relative, Line(node),
                            EvidenceSourceType.ProjectFile, "Project declares target framework: " + normalized.ToLowerInvariant() + ".", cancellationToken);
                    }
                }
            }
            else if (node.Name.LocalName == "ProjectReference")
            {
                RecordReference(node.Attribute("Include")?.Value, relative, root, inventoried,
                    evidence, diagnostics, "dotnet.project.reference",
                    "Project declares build-time ProjectReference to: ", Line(node), cancellationToken);
            }
            else if (node.Name.LocalName == "PackageReference")
            {
                string? package = node.Attribute("Include")?.Value;
                if (package is not null)
                {
                    foreach (string integration in IntegrationNames)
                    {
                        if (KnownPackage(integration, package))
                        {
                            Add(evidence, "dotnet.integration." + integration + ".candidate",
                                relative, Line(node), EvidenceSourceType.ProjectFile,
                                "PackageReference suggests " + integration + " integration; no runtime connection is proven.", cancellationToken);
                        }
                    }

                    if (package.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                        || package.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase)
                        || package.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase))
                    {
                        Add(evidence, "dotnet.project.testCandidate", relative, Line(node),
                            EvidenceSourceType.ProjectFile, "Test framework reference suggests test project; not evaluated.", cancellationToken);
                    }
                }
            }
        }
    }

    private static void ExtractLegacySolution(
        string content,
        string relative,
        string root,
        HashSet<string> inventoried,
        List<Evidence> evidence,
        List<RepositoryDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        using StringReader reader = new(content);
        int number = 0;
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            number++;
            // Only Project() declarations are considered. Ignore solution-folder pseudo-projects.
            if (!line.TrimStart().StartsWith("Project(", StringComparison.Ordinal))
            {
                continue;
            }

            Match match = Regex.Match(line, "^Project\\([^)]*\\)\\s*=\\s*\"[^\"]*\",\\s*\"(?<path>[^\"]+\\.(?:csproj|fsproj))\"",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (match.Success)
            {
                RecordReference(match.Groups["path"].Value, relative, root, inventoried,
                    evidence, diagnostics, "dotnet.solution.project",
                    "Solution declares an inventoried project: ", number, cancellationToken);
            }
        }
    }

    private static void ExtractSourceSignals(
        string content,
        string relative,
        List<Evidence> evidence,
        List<RepositoryDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        using StringReader reader = new(content);
        int lineNumber = 0;
        int count = 0;
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (count >= MaxEvidencePerFile)
            {
                diagnostics.Add(new RepositoryDiagnostic("extract.fileEvidenceLimit", DiagnosticSeverity.Warning,
                    relative, "Per-file candidate limit reached; further source lines were not inspected."));
                break;
            }

            if (HttpSignals.Any(signal => line.Contains(signal, StringComparison.Ordinal)))
            {
                Add(evidence, "dotnet.runtime.http.candidate", relative, lineNumber,
                    EvidenceSourceType.SourceCode, "Source contains a recognizable HTTP host or route API call; runtime usage is unverified.", cancellationToken);
                count++;
            }

            if (WorkerSignals.Any(signal => line.Contains(signal, StringComparison.Ordinal)))
            {
                Add(evidence, "dotnet.runtime.worker.candidate", relative, lineNumber,
                    EvidenceSourceType.SourceCode, "Source contains a recognizable worker-host API or type; runtime execution is unverified.", cancellationToken);
                count++;
            }

            foreach (string integration in IntegrationNames)
            {
                if (line.Contains(integration, StringComparison.OrdinalIgnoreCase))
                {
                    Add(evidence, "dotnet.integration." + integration + ".candidate", relative, lineNumber,
                        EvidenceSourceType.SourceCode, "Source mentions " + integration + "; runtime connection is unverified.", cancellationToken);
                    count++;
                }
            }
        }
    }

    private static bool KnownPackage(string integration, string name) =>
        integration switch
        {
            "postgresql" => name.Equals("Npgsql", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Npgsql.", StringComparison.OrdinalIgnoreCase),
            "rabbitmq" => name.Equals("RabbitMQ.Client", StringComparison.OrdinalIgnoreCase),
            "redis" => name.Equals("StackExchange.Redis", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Microsoft.Extensions.Caching.StackExchangeRedis", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static void RecordReference(
        string? include,
        string source,
        string root,
        HashSet<string> inventoried,
        List<Evidence> evidence,
        List<RepositoryDiagnostic> diagnostics,
        string category,
        string prefix,
        int? line,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(include) || include.Contains("$(", StringComparison.Ordinal))
        {
            diagnostics.Add(new RepositoryDiagnostic("extract.unresolvedReference", DiagnosticSeverity.Info,
                source, "A project reference could not be resolved without MSBuild evaluation."));
            return;
        }

        try
        {
            string absoluteSource = Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar));
            string baseDirectory = Path.GetDirectoryName(absoluteSource)!;
            string absoluteTarget = Path.GetFullPath(Path.Combine(baseDirectory,
                include.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            string target = Path.GetRelativePath(root, absoluteTarget).Replace('\\', '/');
            if (!ContractValidator.IsNormalizedRelativePath(target))
            {
                diagnostics.Add(new RepositoryDiagnostic("extract.outsideRootReference", DiagnosticSeverity.Warning,
                    source, "Reference resolves outside the authorized root and was ignored."));
                return;
            }

            if (!inventoried.Contains(target))
            {
                diagnostics.Add(new RepositoryDiagnostic("extract.missingReference", DiagnosticSeverity.Info,
                    source, "Project reference target is not part of the accepted inventory."));
                return;
            }

            Add(evidence, category, source, line,
                category == "dotnet.project.reference" ? EvidenceSourceType.ProjectFile : EvidenceSourceType.Manifest,
                prefix + target + ".", cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostics.Add(new RepositoryDiagnostic("extract.unresolvedReference", DiagnosticSeverity.Info,
                source, "A project reference could not be resolved without MSBuild evaluation."));
        }
    }

    private static int? Line(XObject element) =>
        (element as IXmlLineInfo)?.HasLineInfo() == true
            ? (element as IXmlLineInfo)?.LineNumber
            : null;

    private static void Add(
        List<Evidence> evidence,
        string category,
        string path,
        int? line,
        EvidenceSourceType sourceType,
        string description,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (evidence.Count >= MaxEvidence)
        {
            return;
        }

        string id = StableIds.ForEvidence(category, path, line, description);
        if (evidence.All(item => item.Id != id))
        {
            evidence.Add(new Evidence(id, category, path, line, sourceType, description));
        }
    }
}

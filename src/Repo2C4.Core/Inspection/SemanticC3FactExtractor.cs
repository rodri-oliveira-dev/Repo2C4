using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Core.Inspection;

/// <summary>
/// Extracts bounded structural C# facts for later Semantic C3 classification.
/// It performs no build, project evaluation, code execution, networking or runtime-flow inference.
/// </summary>
public static class SemanticC3FactExtractor
{
    private const int MaxIdentifierLength = 128;
    private const int MaxAttributesPerSymbol = 32;
    private const int MaxBaseTypesPerType = 32;
    private const int MaxEndpointCallLength = 4_096;

    private static readonly Regex NamespaceRegex = CreateRegex(
        @"\bnamespace\s+(?<name>[A-Za-z_][A-Za-z0-9_]{0,127}(?:\.[A-Za-z_][A-Za-z0-9_]{0,127})*)\s*(?:;|\{)");

    private static readonly Regex TypeRegex = CreateRegex(
        @"(?m)^[ \t]*(?<attrs>(?:[ \t]*\[[^\]\r\n]+\][ \t]*\r?\n)*)" +
        @"[ \t]*(?:(?:public|internal|private|protected|abstract|sealed|static|partial|readonly|ref)\s+)*" +
        @"(?<kind>class|interface|struct|record(?:\s+class|\s+struct)?)\s+" +
        @"(?<name>[A-Za-z_][A-Za-z0-9_]{0,127})(?:\s*<[^>{;\r\n]+>)?" +
        @"(?:\s*:\s*(?<bases>[^\{\r\n]+))?");

    private static readonly Regex MethodRegex = CreateRegex(
        @"(?m)^[ \t]*(?<attrs>(?:[ \t]*\[[^\]\r\n]+\][ \t]*\r?\n)*)" +
        @"[ \t]*(?:(?:public|private|protected|internal|static|virtual|override|abstract|async|sealed|new|partial|extern|unsafe|required)\s+)*" +
        @"(?<return>[A-Za-z_][A-Za-z0-9_?.<>\[\],]{0,255})\s+" +
        @"(?<name>[A-Za-z_][A-Za-z0-9_]{0,127})\s*\((?<params>[^()\r\n]*)\)\s*(?<tail>=>|\{|;)");

    private static readonly Regex ConstructorRegex = CreateRegex(
        @"(?m)^[ \t]*(?<attrs>(?:[ \t]*\[[^\]\r\n]+\][ \t]*\r?\n)*)" +
        @"[ \t]*(?:(?:public|private|protected|internal|static|extern|unsafe)\s+)*" +
        @"(?<name>[A-Za-z_][A-Za-z0-9_]{0,127})\s*\((?<params>[^()\r\n]*)\)\s*(?<tail>=>|\{|;)");

    private static readonly Regex DiRegistrationRegex = CreateRegex(
        @"\bAdd(?<lifetime>Scoped|Singleton|Transient)\s*<\s*" +
        @"(?<service>[A-Za-z_][A-Za-z0-9_.<>?,]{0,255})\s*" +
        @"(?:,\s*(?<implementation>[A-Za-z_][A-Za-z0-9_.<>?,]{0,255})\s*)?>\s*\(");

    private static readonly Regex HostedRegistrationRegex = CreateRegex(
        @"\bAddHostedService\s*<\s*(?<type>[A-Za-z_][A-Za-z0-9_.<>?,]{0,255})\s*>\s*\(");

    private static readonly Regex MinimalApiRegex = CreateRegex(
        @"\b(?<api>MapGet|MapPost|MapPut|MapDelete|MapPatch|MapMethods)\s*\(");

    private static readonly Regex StaticInvocationRegex = CreateRegex(
        @"\b(?<type>[A-Za-z_][A-Za-z0-9_]{0,127})\s*\.\s*" +
        @"(?<method>[A-Za-z_][A-Za-z0-9_]{0,127})\s*\(");

    private static readonly Regex AttributeNameRegex = CreateRegex(
        @"\b(?<name>[A-Za-z_][A-Za-z0-9_]{0,127})(?:Attribute)?\b");

    private static readonly Regex EndpointHandlerRegex = CreateRegex(
        @"^(?<type>[A-Za-z_][A-Za-z0-9_.]{0,255})\.(?<method>[A-Za-z_][A-Za-z0-9_]{0,127})$");

    public static SemanticC3FactSet Extract(
        SemanticC3FactExtractionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        Stopwatch stopwatch = Stopwatch.StartNew();
        DiagnosticAccumulator diagnostics = new(options.MaxDiagnostics);
        RepositorySnapshot inventory = RepositoryScanner.Scan(options.ScanOptions, cancellationToken);

        if (CheckTime(stopwatch, options, diagnostics))
        {
            return CreateResult([], diagnostics);
        }

        string root = Path.GetFullPath(options.ScanOptions.RootPath);
        string[] projects =
        [
            .. inventory.Files
                .Where(file => Path.GetExtension(file.RelativePath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                .Select(file => file.RelativePath)
                .OrderBy(path => path, StringComparer.Ordinal),
        ];

        RepositoryFile[] sources =
        [
            .. inventory.Files
                .Where(file => Path.GetExtension(file.RelativePath).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal),
        ];

        if (sources.Length > options.MaxCSharpFiles)
        {
            diagnostics.Add(
                "semanticC3.fileLimit",
                DiagnosticSeverity.Warning,
                null,
                "C# file budget reached; remaining source files were not analyzed.");
            sources = [.. sources.Take(options.MaxCSharpFiles)];
        }

        List<SourceUnit> units = [];
        long acceptedBytes = 0;

        foreach (RepositoryFile source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CheckTime(stopwatch, options, diagnostics))
            {
                break;
            }

            if (source.SizeBytes > options.MaxTotalSourceBytes - acceptedBytes)
            {
                diagnostics.Add(
                    "semanticC3.totalBytesLimit",
                    DiagnosticSeverity.Warning,
                    null,
                    "Semantic C3 source-byte budget reached; remaining source files were not analyzed.");
                break;
            }

            string? project = FindProject(source.RelativePath, projects, out bool ambiguous);
            if (project is null)
            {
                diagnostics.Add(
                    "semanticC3.projectMissing",
                    DiagnosticSeverity.Info,
                    source.RelativePath,
                    "C# source has no inventoried containing project and was not semantically analyzed.");
                continue;
            }

            if (ambiguous)
            {
                diagnostics.Add(
                    "semanticC3.projectAmbiguous",
                    DiagnosticSeverity.Info,
                    source.RelativePath,
                    "Multiple containing projects have the same scope; the deterministic ordinal-first project was used.");
            }

            (string? content, string? error) = RepositoryFileReader.Read(
                root,
                source,
                options.ScanOptions,
                cancellationToken);

            if (error is not null)
            {
                diagnostics.Add(
                    "semanticC3.readUnavailable",
                    DiagnosticSeverity.Warning,
                    source.RelativePath,
                    "Inventoried C# source could not be safely read and was not analyzed.");
                continue;
            }

            acceptedBytes += source.SizeBytes;
            string masked = MaskSource(content!, out bool complete);

            try
            {
                SourceUnit unit = ParseUnit(project, source.RelativePath, masked, cancellationToken);
                units.Add(unit);

                if (!complete || unit.HasUnbalancedTypeBody)
                {
                    diagnostics.Add(
                        "semanticC3.partialSyntax",
                        DiagnosticSeverity.Info,
                        source.RelativePath,
                        "C# source is incomplete or partially parseable; only bounded structural facts were retained.");
                }
            }
            catch (RegexMatchTimeoutException)
            {
                diagnostics.Add(
                    "semanticC3.parseTimeout",
                    DiagnosticSeverity.Warning,
                    source.RelativePath,
                    "C# structural parsing exceeded its bounded regex budget; the source file was skipped.");
            }
        }

        FactAccumulator facts = new(options, diagnostics);
        Dictionary<string, Dictionary<string, TypeDeclaration>> projectTypes = BuildProjectTypeIndex(units);

        foreach (SourceUnit unit in units.OrderBy(item => item.SourcePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CheckTime(stopwatch, options, diagnostics) || facts.FactLimitReached)
            {
                break;
            }

            try
            {
                AnalyzeUnit(unit, projectTypes, facts, diagnostics, cancellationToken);
            }
            catch (RegexMatchTimeoutException)
            {
                diagnostics.Add(
                    "semanticC3.analysisTimeout",
                    DiagnosticSeverity.Warning,
                    unit.SourcePath,
                    "C# structural analysis exceeded its bounded regex budget; remaining facts for the source file were skipped.");
            }
        }

        return CreateResult(
            [.. facts.Items.OrderBy(item => item.Id, StringComparer.Ordinal)],
            diagnostics);
    }

    private static void AnalyzeUnit(
        SourceUnit unit,
        Dictionary<string, Dictionary<string, TypeDeclaration>> projectTypes,
        FactAccumulator facts,
        DiagnosticAccumulator diagnostics,
        CancellationToken cancellationToken)
    {
        Dictionary<string, TypeDeclaration> knownTypes = projectTypes[unit.ProjectPath];

        foreach (TypeDeclaration type in unit.Types)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (facts.FactLimitReached)
            {
                return;
            }

            SemanticC3SourceSymbolIdentity typeSymbol = Identity(unit.ProjectPath, type.SymbolId);
            facts.AddSymbol(Fact(
                unit,
                type.Line,
                typeSymbol,
                SemanticC3FactKind.TypeDeclaration,
                "semantic.symbol.type",
                "Declares " + type.Kind + " symbol " + type.Name + ".",
                null));

            foreach (string attribute in type.Attributes.Take(MaxAttributesPerSymbol))
            {
                facts.AddObservation(Fact(
                    unit,
                    type.Line,
                    typeSymbol,
                    SemanticC3FactKind.Attribute,
                    "semantic.symbol.attribute",
                    "Type declares attribute " + attribute + ".",
                    "A:" + attribute));
            }

            int baseIndex = 0;
            foreach (string baseType in type.BaseTypes.Take(MaxBaseTypesPerType))
            {
                string simple = SimpleTypeName(baseType);
                bool interfaceType = knownTypes.TryGetValue(simple, out TypeDeclaration? local)
                    ? local.IsInterface
                    : LooksLikeInterface(simple);

                facts.AddObservation(Fact(
                    unit,
                    type.Line,
                    typeSymbol,
                    interfaceType ? SemanticC3FactKind.ImplementedInterface : SemanticC3FactKind.BaseType,
                    interfaceType ? "semantic.symbol.interface" : "semantic.symbol.baseType",
                    interfaceType
                        ? "Type implements interface " + simple + "."
                        : "Type derives from base type " + simple + ".",
                    (interfaceType ? "I:" : "B:") + simple + ":" + baseIndex));
                baseIndex++;
            }

            if (IsController(type))
            {
                facts.AddObservation(Fact(
                    unit,
                    type.Line,
                    typeSymbol,
                    SemanticC3FactKind.HttpBoundary,
                    "semantic.host.controller",
                    "Type is structurally identifiable as an ASP.NET Core controller.",
                    "controller"));
            }

            if (type.BaseTypes.Any(item => SimpleTypeName(item) == "BackgroundService" || SimpleTypeName(item) == "IHostedService"))
            {
                facts.AddObservation(Fact(
                    unit,
                    type.Line,
                    typeSymbol,
                    SemanticC3FactKind.HostedService,
                    "semantic.host.backgroundService",
                    "Type declares a BackgroundService or IHostedService boundary.",
                    "hostedService"));
            }

            if (type.BaseTypes.Any(item => SimpleTypeName(item) == "DbContext"))
            {
                facts.AddObservation(Fact(
                    unit,
                    type.Line,
                    typeSymbol,
                    SemanticC3FactKind.PersistenceCandidate,
                    "semantic.persistence.dbContext",
                    "Type derives from DbContext and is a persistence-boundary candidate.",
                    "dbContext"));
            }

            if (!type.IsInterface && IsRepositoryImplementationSignal(type))
            {
                facts.AddObservation(Fact(
                    unit,
                    type.Line,
                    typeSymbol,
                    SemanticC3FactKind.PersistenceCandidate,
                    "semantic.persistence.repositoryImplementation",
                    "Concrete type implements or declares a repository-shaped persistence abstraction.",
                    "repository"));
            }

            if (IsMessagingAbstraction(type))
            {
                facts.AddObservation(Fact(
                    unit,
                    type.Line,
                    typeSymbol,
                    SemanticC3FactKind.MessagingCandidate,
                    "semantic.messaging.abstraction",
                    "Type or implemented abstraction has a messaging publisher/consumer role signal.",
                    "messaging"));
            }

            foreach (MethodDeclaration method in type.Methods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SemanticC3SourceSymbolIdentity methodSymbol = Identity(unit.ProjectPath, method.SymbolId);
                facts.AddSymbol(Fact(
                    unit,
                    method.Line,
                    methodSymbol,
                    method.IsConstructor
                        ? SemanticC3FactKind.ConstructorDeclaration
                        : SemanticC3FactKind.MethodDeclaration,
                    method.IsConstructor ? "semantic.symbol.constructor" : "semantic.symbol.method",
                    method.IsConstructor
                        ? "Declares constructor for " + type.Name + "."
                        : "Declares method " + method.Name + ".",
                    null));

                for (int parameterIndex = 0; parameterIndex < method.Parameters.Length; parameterIndex++)
                {
                    ParameterDeclaration parameter = method.Parameters[parameterIndex];
                    facts.AddObservation(Fact(
                        unit,
                        method.Line,
                        methodSymbol,
                        SemanticC3FactKind.MethodParameter,
                        "semantic.symbol.parameter",
                        "Method declares parameter type " + parameter.Type + ".",
                        "T:" + parameter.Type + ":" + parameterIndex));
                }

                foreach (string attribute in method.Attributes.Take(MaxAttributesPerSymbol))
                {
                    facts.AddObservation(Fact(
                        unit,
                        method.Line,
                        methodSymbol,
                        SemanticC3FactKind.Attribute,
                        "semantic.symbol.attribute",
                        "Method declares attribute " + attribute + ".",
                        "A:" + attribute));
                }

                if (method.Attributes.Any(IsHttpAttribute))
                {
                    facts.AddObservation(Fact(
                        unit,
                        method.Line,
                        methodSymbol,
                        SemanticC3FactKind.ControllerAction,
                        "semantic.host.controllerAction",
                        "Method has an ASP.NET Core HTTP action attribute.",
                        "httpAction"));
                }

                if (method.IsConstructor)
                {
                    int parameterIndex = 0;
                    foreach (ParameterDeclaration parameter in method.Parameters)
                    {
                        facts.AddRelation(Fact(
                            unit,
                            method.Line,
                            methodSymbol,
                            SemanticC3FactKind.ConstructorInjection,
                            "semantic.wiring.constructorInjection",
                            "Constructor depends on parameter type " + parameter.Type + ".",
                            "T:" + parameter.Type + ":" + parameterIndex));
                        parameterIndex++;
                    }
                }

                if (method.BodyStart >= 0 && method.BodyEnd > method.BodyStart)
                {
                    foreach (Match invocation in StaticInvocationRegex.Matches(
                                 unit.MaskedSource[method.BodyStart..method.BodyEnd]))
                    {
                        string typeName = invocation.Groups["type"].Value;
                        string methodName = invocation.Groups["method"].Value;
                        if (!knownTypes.TryGetValue(typeName, out TypeDeclaration? targetType))
                        {
                            continue;
                        }

                        string related = "M:" + targetType.QualifiedName + "." + methodName;
                        facts.AddRelation(Fact(
                            unit,
                            unit.LineAt(method.BodyStart + invocation.Index),
                            methodSymbol,
                            SemanticC3FactKind.SymbolInvocation,
                            "semantic.collaboration.staticInvocation",
                            "Method invokes a statically referenced symbol on " + typeName + ".",
                            related));
                    }
                }
            }
        }

        AnalyzeTopLevelSignals(unit, facts, cancellationToken);
    }

    private static void AnalyzeTopLevelSignals(
        SourceUnit unit,
        FactAccumulator facts,
        CancellationToken cancellationToken)
    {
        int routeOrdinal = 0;
        foreach (Match match in MinimalApiRegex.Matches(unit.MaskedSource))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemanticC3SourceSymbolIdentity source = unit.EnclosingSymbol(match.Index)
                ?? Identity(unit.ProjectPath, "M:<global>.Program.<top-level>()");
            string api = match.Groups["api"].Value;

            facts.AddObservation(Fact(
                unit,
                unit.LineAt(match.Index),
                source,
                SemanticC3FactKind.HttpBoundary,
                "semantic.host.minimalApi",
                "Source invokes ASP.NET Core " + api + " route mapping.",
                "route:" + api + ":" + routeOrdinal));

            AnalyzeEndpointDetails(unit, match, source, facts);
            routeOrdinal++;
        }

        int registrationOrdinal = 0;
        foreach (Match match in DiRegistrationRegex.Matches(unit.MaskedSource))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemanticC3SourceSymbolIdentity source = unit.EnclosingSymbol(match.Index)
                ?? Identity(unit.ProjectPath, "M:<global>.Program.<top-level>()");
            string lifetime = match.Groups["lifetime"].Value;
            string service = NormalizeTypeToken(match.Groups["service"].Value);
            string implementation = match.Groups["implementation"].Success
                ? NormalizeTypeToken(match.Groups["implementation"].Value)
                : service;

            facts.AddRelation(Fact(
                unit,
                unit.LineAt(match.Index),
                source,
                SemanticC3FactKind.DependencyInjectionRegistration,
                "semantic.wiring.diRegistration",
                "DI registers " + service + " to " + implementation + " with " + lifetime + " lifetime.",
                "DI:" + lifetime + ":" + service + "->" + implementation + ":" + registrationOrdinal));
            registrationOrdinal++;
        }

        int hostedOrdinal = 0;
        foreach (Match match in HostedRegistrationRegex.Matches(unit.MaskedSource))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemanticC3SourceSymbolIdentity source = unit.EnclosingSymbol(match.Index)
                ?? Identity(unit.ProjectPath, "M:<global>.Program.<top-level>()");
            string hostedType = NormalizeTypeToken(match.Groups["type"].Value);

            facts.AddRelation(Fact(
                unit,
                unit.LineAt(match.Index),
                source,
                SemanticC3FactKind.HostedService,
                "semantic.wiring.hostedServiceRegistration",
                "DI registers hosted service type " + hostedType + ".",
                "HOST:" + hostedType + ":" + hostedOrdinal));
            hostedOrdinal++;
        }
    }

    private static void AnalyzeEndpointDetails(
        SourceUnit unit,
        Match routeMatch,
        SemanticC3SourceSymbolIdentity source,
        FactAccumulator facts)
    {
        int openParenthesis = routeMatch.Index + routeMatch.Length - 1;
        int closeParenthesis = FindMatchingParenthesisBounded(unit.MaskedSource, openParenthesis);
        if (closeParenthesis < 0)
        {
            return;
        }

        string arguments = unit.MaskedSource[(openParenthesis + 1)..closeParenthesis];
        int lambdaArrow = arguments.IndexOf("=>", StringComparison.Ordinal);
        if (lambdaArrow >= 0)
        {
            int parameterClose = arguments.LastIndexOf(')', lambdaArrow);
            if (parameterClose < 0)
            {
                return;
            }

            int parameterOpen = FindMatchingOpenParenthesis(arguments, parameterClose);
            if (parameterOpen < 0)
            {
                return;
            }

            ParameterDeclaration[] parameters = ParseParameters(
                arguments[(parameterOpen + 1)..parameterClose]);

            for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
            {
                ParameterDeclaration parameter = parameters[parameterIndex];
                facts.AddRelation(Fact(
                    unit,
                    unit.LineAt(routeMatch.Index),
                    source,
                    SemanticC3FactKind.EndpointDependency,
                    "semantic.wiring.endpointDependency",
                    "HTTP endpoint lambda depends on parameter type " + parameter.Type + ".",
                    "T:" + parameter.Type + ":" + parameterIndex));
            }

            return;
        }

        string? handlerArgument = LastTopLevelArgument(arguments);
        if (handlerArgument is null)
        {
            return;
        }

        Match handler = EndpointHandlerRegex.Match(handlerArgument);
        if (!handler.Success)
        {
            return;
        }

        string typeName = handler.Groups["type"].Value;
        string methodName = handler.Groups["method"].Value;
        facts.AddRelation(Fact(
            unit,
            unit.LineAt(routeMatch.Index),
            source,
            SemanticC3FactKind.EndpointHandler,
            "semantic.host.endpointHandler",
            "HTTP endpoint maps to explicit handler " + typeName + "." + methodName + ".",
            "M:" + typeName + "." + methodName));
    }

    private static int FindMatchingParenthesisBounded(string source, int openParenthesis)
    {
        int depth = 0;
        int limit = Math.Min(source.Length, openParenthesis + MaxEndpointCallLength);

        for (int index = openParenthesis; index < limit; index++)
        {
            if (source[index] == '(')
            {
                depth++;
            }
            else if (source[index] == ')' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindMatchingOpenParenthesis(string value, int closeParenthesis)
    {
        int depth = 0;
        for (int index = closeParenthesis; index >= 0; index--)
        {
            if (value[index] == ')')
            {
                depth++;
            }
            else if (value[index] == '(' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static string? LastTopLevelArgument(string value)
    {
        int start = 0;
        int round = 0;
        int square = 0;
        int curly = 0;
        int angle = 0;

        for (int index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '(':
                    round++;
                    break;
                case ')':
                    round = Math.Max(0, round - 1);
                    break;
                case '[':
                    square++;
                    break;
                case ']':
                    square = Math.Max(0, square - 1);
                    break;
                case '{':
                    curly++;
                    break;
                case '}':
                    curly = Math.Max(0, curly - 1);
                    break;
                case '<':
                    angle++;
                    break;
                case '>':
                    angle = Math.Max(0, angle - 1);
                    break;
                case ',' when round == 0 && square == 0 && curly == 0 && angle == 0:
                    start = index + 1;
                    break;
            }
        }

        string candidate = value[start..].Trim();
        return candidate.Length == 0 ? null : candidate;
    }

    private static SourceUnit ParseUnit(
        string projectPath,
        string sourcePath,
        string masked,
        CancellationToken cancellationToken)
    {
        int[] lineStarts = BuildLineStarts(masked);
        int[] braceDepth = BuildBraceDepth(masked);
        Match[] namespaces = [.. NamespaceRegex.Matches(masked).Cast<Match>()];
        List<TypeDeclaration> types = [];
        bool unbalanced = false;

        foreach (Match match in TypeRegex.Matches(masked))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string kind = match.Groups["kind"].Value.Replace(" ", string.Empty, StringComparison.Ordinal);
            string name = match.Groups["name"].Value;
            string ns = NamespaceAt(namespaces, match.Index);
            string qualified = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            string[] bases = SplitTypeList(match.Groups["bases"].Value);
            string[] attributes = ExtractAttributes(match.Groups["attrs"].Value);

            int openBrace = masked.IndexOf('{', match.Index + match.Length);
            int bodyStart = -1;
            int bodyEnd = -1;
            int bodyDepth = -1;

            if (openBrace >= 0)
            {
                int closeBrace = FindMatchingBrace(masked, openBrace);
                bodyStart = openBrace + 1;
                bodyEnd = closeBrace >= 0 ? closeBrace : masked.Length;
                bodyDepth = braceDepth[bodyStart];
                unbalanced |= closeBrace < 0;
            }

            TypeDeclaration type = new(
                name,
                qualified,
                kind,
                "interface".Equals(kind, StringComparison.Ordinal),
                LineAt(lineStarts, match.Index),
                match.Index,
                bodyStart,
                bodyEnd,
                bodyDepth,
                bases,
                attributes,
                []);

            if (bodyStart >= 0)
            {
                type = type with
                {
                    Methods = ParseMethods(masked, braceDepth, lineStarts, type),
                };
            }

            types.Add(type);
        }

        return new SourceUnit(
            projectPath,
            sourcePath,
            masked,
            lineStarts,
            [.. types.OrderBy(type => type.StartIndex)],
            unbalanced);
    }

    private static ImmutableArray<MethodDeclaration> ParseMethods(
        string masked,
        int[] braceDepth,
        int[] lineStarts,
        TypeDeclaration type)
    {
        if (type.BodyStart < 0 || type.BodyEnd <= type.BodyStart)
        {
            return [];
        }

        string body = masked[type.BodyStart..type.BodyEnd];
        List<MethodDeclaration> methods = [];

        foreach (Match match in MethodRegex.Matches(body))
        {
            int absolute = type.BodyStart + match.Index;
            if (braceDepth[absolute] != type.BodyDepth)
            {
                continue;
            }

            string name = match.Groups["name"].Value;
            if (IsControlKeyword(name))
            {
                continue;
            }

            ParameterDeclaration[] parameters = ParseParameters(match.Groups["params"].Value);
            string symbolId = MethodSymbolId(type, name, parameters, isConstructor: false);
            (int start, int end) = MethodBody(masked, absolute, match);

            methods.Add(new MethodDeclaration(
                name,
                symbolId,
                false,
                LineAt(lineStarts, absolute),
                ExtractAttributes(match.Groups["attrs"].Value),
                parameters,
                start,
                end,
                absolute));
        }

        foreach (Match match in ConstructorRegex.Matches(body))
        {
            int absolute = type.BodyStart + match.Index;
            if (braceDepth[absolute] != type.BodyDepth ||
                !string.Equals(match.Groups["name"].Value, type.Name, StringComparison.Ordinal))
            {
                continue;
            }

            ParameterDeclaration[] parameters = ParseParameters(match.Groups["params"].Value);
            string symbolId = MethodSymbolId(type, "#ctor", parameters, isConstructor: true);
            (int start, int end) = MethodBody(masked, absolute, match);

            methods.Add(new MethodDeclaration(
                type.Name,
                symbolId,
                true,
                LineAt(lineStarts, absolute),
                ExtractAttributes(match.Groups["attrs"].Value),
                parameters,
                start,
                end,
                absolute));
        }

        return
        [
            .. methods
                .GroupBy(method => method.SymbolId, StringComparer.Ordinal)
                .Select(group => group.OrderBy(method => method.StartIndex).First())
                .OrderBy(method => method.StartIndex),
        ];
    }

    private static (int Start, int End) MethodBody(string masked, int absoluteStart, Match relativeMatch)
    {
        Group tail = relativeMatch.Groups["tail"];
        int tailAbsolute = absoluteStart + tail.Index - relativeMatch.Index;

        if (tail.Value != "{")
        {
            return (-1, -1);
        }

        int close = FindMatchingBrace(masked, tailAbsolute);
        return close >= 0 ? (tailAbsolute + 1, close) : (tailAbsolute + 1, masked.Length);
    }

    private static Dictionary<string, Dictionary<string, TypeDeclaration>> BuildProjectTypeIndex(
        IEnumerable<SourceUnit> units)
    {
        Dictionary<string, Dictionary<string, TypeDeclaration>> result = new(StringComparer.Ordinal);

        foreach (SourceUnit unit in units)
        {
            if (!result.TryGetValue(unit.ProjectPath, out Dictionary<string, TypeDeclaration>? types))
            {
                types = new Dictionary<string, TypeDeclaration>(StringComparer.Ordinal);
                result.Add(unit.ProjectPath, types);
            }

            foreach (TypeDeclaration type in unit.Types)
            {
                types.TryAdd(type.Name, type);
                types.TryAdd(type.QualifiedName, type);
            }
        }

        return result;
    }

    private static string? FindProject(string sourcePath, string[] projects, out bool ambiguous)
    {
        ambiguous = false;
        string sourceDirectory = DirectoryPart(sourcePath);

        (string Path, int Depth)[] candidates =
        [
            .. projects
                .Select(project => (Path: project, Depth: DirectoryDepth(DirectoryPart(project))))
                .Where(candidate => IsWithin(sourceDirectory, DirectoryPart(candidate.Path)))
                .OrderByDescending(candidate => candidate.Depth)
                .ThenBy(candidate => candidate.Path, StringComparer.Ordinal),
        ];

        if (candidates.Length == 0)
        {
            return null;
        }

        int bestDepth = candidates[0].Depth;
        ambiguous = candidates.Count(candidate => candidate.Depth == bestDepth) > 1;
        return candidates[0].Path;
    }

    private static bool IsWithin(string sourceDirectory, string projectDirectory) =>
        projectDirectory.Length == 0 ||
        sourceDirectory.Equals(projectDirectory, StringComparison.Ordinal) ||
        sourceDirectory.StartsWith(projectDirectory + "/", StringComparison.Ordinal);

    private static string DirectoryPart(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static int DirectoryDepth(string path) =>
        path.Length == 0 ? 0 : path.Count(character => character == '/') + 1;

    private static string NamespaceAt(Match[] namespaces, int position)
    {
        string current = string.Empty;
        foreach (Match item in namespaces)
        {
            if (item.Index > position)
            {
                break;
            }

            current = item.Groups["name"].Value;
        }

        return current;
    }

    private static ParameterDeclaration[] ParseParameters(string value)
    {
        List<ParameterDeclaration> result = [];

        foreach (string item in SplitCommaSeparated(value))
        {
            string parameter = RemoveAttributeBlocks(item).Trim();
            int equals = parameter.IndexOf('=');
            if (equals >= 0)
            {
                parameter = parameter[..equals].Trim();
            }

            string[] tokens = parameter
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token is not "ref" and not "out" and not "in" and not "params"
                    and not "this" and not "scoped")
                .ToArray();

            if (tokens.Length < 2)
            {
                continue;
            }

            string type = NormalizeTypeToken(string.Join(" ", tokens[..^1]));
            string name = tokens[^1].Trim();
            if (IsIdentifier(name) && type.Length > 0)
            {
                result.Add(new ParameterDeclaration(type, name));
            }
        }

        return [.. result];
    }

    private static string[] SplitTypeList(string value) =>
        [.. SplitCommaSeparated(value)
            .Select(item => NormalizeTypeToken(item))
            .Where(item => item.Length > 0)
            .Take(MaxBaseTypesPerType)];

    private static IEnumerable<string> SplitCommaSeparated(string value)
    {
        int start = 0;
        int angle = 0;
        int square = 0;

        for (int i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '<':
                    angle++;
                    break;
                case '>':
                    angle = Math.Max(0, angle - 1);
                    break;
                case '[':
                    square++;
                    break;
                case ']':
                    square = Math.Max(0, square - 1);
                    break;
                case ',' when angle == 0 && square == 0:
                    yield return value[start..i];
                    start = i + 1;
                    break;
            }
        }

        if (start <= value.Length)
        {
            yield return value[start..];
        }
    }

    private static string[] ExtractAttributes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        List<string> attributes = [];
        foreach (Match block in Regex.Matches(
                     value,
                     @"\[(?<body>[^\]\r\n]+)\]",
                     RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                     TimeSpan.FromMilliseconds(100)))
        {
            foreach (string item in SplitCommaSeparated(block.Groups["body"].Value))
            {
                Match name = AttributeNameRegex.Match(item);
                if (!name.Success)
                {
                    continue;
                }

                string attribute = name.Groups["name"].Value;
                if (attribute.EndsWith("Attribute", StringComparison.Ordinal))
                {
                    attribute = attribute[..^"Attribute".Length];
                }

                if (!attributes.Contains(attribute, StringComparer.Ordinal))
                {
                    attributes.Add(attribute);
                }

                if (attributes.Count >= MaxAttributesPerSymbol)
                {
                    return [.. attributes];
                }
            }
        }

        return [.. attributes];
    }

    private static string RemoveAttributeBlocks(string value) =>
        Regex.Replace(
            value,
            @"\[[^\]]*\]",
            string.Empty,
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(100));

    private static string MethodSymbolId(
        TypeDeclaration type,
        string methodName,
        ParameterDeclaration[] parameters,
        bool isConstructor)
    {
        string parametersPart = string.Join(",", parameters.Select(parameter => parameter.Type));
        string name = isConstructor ? "#ctor" : methodName;
        return "M:" + type.QualifiedName + "." + name + "(" + parametersPart + ")";
    }

    private static SemanticC3SourceSymbolIdentity Identity(string projectPath, string symbolId) =>
        new(
            StableIds.ForSemanticC3SourceSymbol(projectPath, symbolId),
            projectPath,
            symbolId);

    private static SemanticC3Fact Fact(
        SourceUnit unit,
        int? line,
        SemanticC3SourceSymbolIdentity source,
        SemanticC3FactKind kind,
        string category,
        string description,
        string? relatedSymbolId)
    {
        string id = StableIds.ForSemanticC3Fact(
            unit.ProjectPath,
            unit.SourcePath,
            source.Id,
            kind,
            category,
            relatedSymbolId ?? string.Empty);

        return new SemanticC3Fact(
            id,
            unit.ProjectPath,
            unit.SourcePath,
            line,
            source,
            kind,
            category,
            description,
            relatedSymbolId);
    }

    private static bool IsController(TypeDeclaration type) =>
        type.Attributes.Contains("ApiController", StringComparer.Ordinal) ||
        type.BaseTypes.Any(item =>
            SimpleTypeName(item) is "Controller" or "ControllerBase");

    private static bool IsRepositoryImplementationSignal(TypeDeclaration type)
    {
        if (type.Name.EndsWith("Repository", StringComparison.Ordinal))
        {
            return true;
        }

        return type.BaseTypes
            .Select(SimpleTypeName)
            .Any(item => item.EndsWith("Repository", StringComparison.Ordinal));
    }

    private static bool IsMessagingAbstraction(TypeDeclaration type)
    {
        IEnumerable<string> candidates = type.BaseTypes
            .Select(SimpleTypeName)
            .Append(type.Name);

        return candidates.Any(item =>
            item.Contains("Publisher", StringComparison.OrdinalIgnoreCase) ||
            item.Contains("Producer", StringComparison.OrdinalIgnoreCase) ||
            item.Contains("Consumer", StringComparison.OrdinalIgnoreCase) ||
            item.Contains("Subscriber", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHttpAttribute(string attribute) =>
        attribute.StartsWith("HttpGet", StringComparison.Ordinal) ||
        attribute.StartsWith("HttpPost", StringComparison.Ordinal) ||
        attribute.StartsWith("HttpPut", StringComparison.Ordinal) ||
        attribute.StartsWith("HttpDelete", StringComparison.Ordinal) ||
        attribute.StartsWith("HttpPatch", StringComparison.Ordinal) ||
        attribute.StartsWith("HttpHead", StringComparison.Ordinal) ||
        attribute.StartsWith("HttpOptions", StringComparison.Ordinal);

    private static bool LooksLikeInterface(string typeName) =>
        typeName.Length >= 2 &&
        typeName[0] == 'I' &&
        char.IsUpper(typeName[1]);

    private static string SimpleTypeName(string type)
    {
        string normalized = NormalizeTypeToken(type);
        int generic = normalized.IndexOf('<');
        if (generic >= 0)
        {
            normalized = normalized[..generic];
        }

        int dot = normalized.LastIndexOf('.');
        return dot >= 0 ? normalized[(dot + 1)..] : normalized;
    }

    private static string NormalizeTypeToken(string value)
    {
        StringBuilder builder = new();
        foreach (char character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            if (char.IsLetterOrDigit(character) ||
                character is '_' or '.' or '<' or '>' or ',' or '?' or '[' or ']')
            {
                builder.Append(character);
            }

            if (builder.Length >= 256)
            {
                break;
            }
        }

        return builder.ToString().Replace("global::", string.Empty, StringComparison.Ordinal);
    }

    private static bool IsIdentifier(string value) =>
        value.Length is > 0 and <= MaxIdentifierLength &&
        (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');

    private static bool IsControlKeyword(string value) =>
        value is "if" or "for" or "foreach" or "while" or "switch" or "catch"
            or "using" or "lock" or "return" or "nameof" or "typeof";

    private static int[] BuildLineStarts(string source)
    {
        List<int> starts = [0];
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return [.. starts];
    }

    private static int LineAt(int[] lineStarts, int index)
    {
        int found = Array.BinarySearch(lineStarts, index);
        int position = found >= 0 ? found : ~found - 1;
        return Math.Max(0, position) + 1;
    }

    private static int[] BuildBraceDepth(string source)
    {
        int[] depth = new int[source.Length + 1];
        for (int i = 0; i < source.Length; i++)
        {
            int next = depth[i];
            if (source[i] == '{')
            {
                next++;
            }
            else if (source[i] == '}')
            {
                next = Math.Max(0, next - 1);
            }

            depth[i + 1] = next;
        }

        return depth;
    }

    private static int FindMatchingBrace(string source, int openBrace)
    {
        int depth = 0;
        for (int i = openBrace; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static string MaskSource(string source, out bool complete)
    {
        char[] chars = source.ToCharArray();
        complete = true;
        int i = 0;

        while (i < chars.Length)
        {
            if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                Mask(chars, i, i + 2);
                i += 2;
                while (i < chars.Length && chars[i] != '\n')
                {
                    chars[i++] = ' ';
                }

                continue;
            }

            if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                Mask(chars, i, i + 2);
                i += 2;
                bool closed = false;
                while (i < chars.Length)
                {
                    if (chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/')
                    {
                        Mask(chars, i, i + 2);
                        i += 2;
                        closed = true;
                        break;
                    }

                    if (chars[i] != '\n' && chars[i] != '\r')
                    {
                        chars[i] = ' ';
                    }

                    i++;
                }

                complete &= closed;
                continue;
            }

            if (chars[i] == (char)39)
            {
                i = MaskQuoted(chars, i, (char)39, verbatim: false, out bool closed);
                complete &= closed;
                continue;
            }

            int quoteRun = QuoteRun(chars, i);
            if (quoteRun >= 3)
            {
                i = MaskRawString(chars, i, quoteRun, out bool closed);
                complete &= closed;
                continue;
            }

            bool verbatim = chars[i] == '"' && i > 0 && chars[i - 1] == '@';
            if (chars[i] == '"')
            {
                i = MaskQuoted(chars, i, '"', verbatim, out bool closed);
                complete &= closed;
                continue;
            }

            i++;
        }

        return new string(chars);
    }

    private static int MaskQuoted(char[] chars, int start, char delimiter, bool verbatim, out bool closed)
    {
        closed = false;
        chars[start] = ' ';
        int i = start + 1;

        while (i < chars.Length)
        {
            if (chars[i] == '\n' || chars[i] == '\r')
            {
                if (delimiter == (char)39)
                {
                    break;
                }

                i++;
                continue;
            }

            if (chars[i] == delimiter)
            {
                if (verbatim && i + 1 < chars.Length && chars[i + 1] == delimiter)
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    i += 2;
                    continue;
                }

                chars[i] = ' ';
                i++;
                closed = true;
                break;
            }

            if (!verbatim && chars[i] == '\\' && i + 1 < chars.Length)
            {
                chars[i] = ' ';
                if (chars[i + 1] != '\n' && chars[i + 1] != '\r')
                {
                    chars[i + 1] = ' ';
                }

                i += 2;
                continue;
            }

            chars[i] = ' ';
            i++;
        }

        return i;
    }

    private static int MaskRawString(char[] chars, int start, int quoteRun, out bool closed)
    {
        closed = false;
        Mask(chars, start, start + quoteRun);
        int i = start + quoteRun;

        while (i < chars.Length)
        {
            int run = QuoteRun(chars, i);
            if (run >= quoteRun)
            {
                Mask(chars, i, i + quoteRun);
                i += quoteRun;
                closed = true;
                break;
            }

            if (chars[i] != '\n' && chars[i] != '\r')
            {
                chars[i] = ' ';
            }

            i++;
        }

        return i;
    }

    private static int QuoteRun(char[] chars, int start)
    {
        if (start >= chars.Length || chars[start] != '"')
        {
            return 0;
        }

        int i = start;
        while (i < chars.Length && chars[i] == '"')
        {
            i++;
        }

        return i - start;
    }

    private static void Mask(char[] chars, int start, int end)
    {
        for (int i = start; i < end && i < chars.Length; i++)
        {
            if (chars[i] != '\n' && chars[i] != '\r')
            {
                chars[i] = ' ';
            }
        }
    }

    private static bool CheckTime(
        Stopwatch stopwatch,
        SemanticC3FactExtractionOptions options,
        DiagnosticAccumulator diagnostics)
    {
        if (stopwatch.Elapsed <= options.Timeout)
        {
            return false;
        }

        diagnostics.Add(
            "semanticC3.timeLimit",
            DiagnosticSeverity.Warning,
            null,
            "Semantic C3 analysis time budget reached; remaining source was not analyzed.");
        return true;
    }

    private static SemanticC3FactSet CreateResult(
        ImmutableArray<SemanticC3Fact> facts,
        DiagnosticAccumulator diagnostics) =>
        new(
            SemanticC3ContractSchema.Version,
            facts,
            diagnostics.Items);

    private static void ValidateOptions(SemanticC3FactExtractionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.ScanOptions);

        if (options.MaxCSharpFiles <= 0 ||
            options.MaxTotalSourceBytes <= 0 ||
            options.MaxSymbols <= 0 ||
            options.MaxRelations <= 0 ||
            options.MaxFacts <= 0 ||
            options.MaxDiagnostics < 2 ||
            options.Timeout <= TimeSpan.Zero ||
            options.Timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Semantic C3 budgets must be positive, diagnostics must allow at least two entries, and timeout must not exceed one minute.");
        }
    }

    private static Regex CreateRegex(string pattern) =>
        new(
            pattern,
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

    private sealed record ParameterDeclaration(string Type, string Name);

    private sealed record MethodDeclaration(
        string Name,
        string SymbolId,
        bool IsConstructor,
        int Line,
        string[] Attributes,
        ParameterDeclaration[] Parameters,
        int BodyStart,
        int BodyEnd,
        int StartIndex);

    private sealed record TypeDeclaration(
        string Name,
        string QualifiedName,
        string Kind,
        bool IsInterface,
        int Line,
        int StartIndex,
        int BodyStart,
        int BodyEnd,
        int BodyDepth,
        string[] BaseTypes,
        string[] Attributes,
        ImmutableArray<MethodDeclaration> Methods)
    {
        public string SymbolId => "T:" + QualifiedName;
    }

    private sealed record SourceUnit(
        string ProjectPath,
        string SourcePath,
        string MaskedSource,
        int[] LineStarts,
        ImmutableArray<TypeDeclaration> Types,
        bool HasUnbalancedTypeBody)
    {
        public int LineAt(int index) => SemanticC3FactExtractor.LineAt(LineStarts, index);

        public SemanticC3SourceSymbolIdentity? EnclosingSymbol(int index)
        {
            MethodDeclaration? method = Types
                .SelectMany(type => type.Methods)
                .Where(candidate =>
                    candidate.BodyStart >= 0 &&
                    index >= candidate.BodyStart &&
                    index <= candidate.BodyEnd)
                .OrderByDescending(candidate => candidate.BodyStart)
                .FirstOrDefault();

            if (method is not null)
            {
                return Identity(ProjectPath, method.SymbolId);
            }

            TypeDeclaration? type = Types
                .Where(candidate =>
                    candidate.BodyStart >= 0 &&
                    index >= candidate.BodyStart &&
                    index <= candidate.BodyEnd)
                .OrderByDescending(candidate => candidate.BodyStart)
                .FirstOrDefault();

            return type is null ? null : Identity(ProjectPath, type.SymbolId);
        }
    }

    private sealed class FactAccumulator(
        SemanticC3FactExtractionOptions options,
        DiagnosticAccumulator diagnostics)
    {
        private readonly HashSet<string> ids = new(StringComparer.Ordinal);
        private int symbolCount;
        private int relationCount;

        public List<SemanticC3Fact> Items { get; } = [];

        public bool FactLimitReached
        {
            get;
            private set;
        }

        public void AddSymbol(SemanticC3Fact fact)
        {
            if (symbolCount >= options.MaxSymbols)
            {
                diagnostics.Add(
                    "semanticC3.symbolLimit",
                    DiagnosticSeverity.Warning,
                    null,
                    "Semantic C3 symbol budget reached; additional symbols were omitted.");
                return;
            }

            if (TryAdd(fact))
            {
                symbolCount++;
            }
        }

        public void AddRelation(SemanticC3Fact fact)
        {
            if (relationCount >= options.MaxRelations)
            {
                diagnostics.Add(
                    "semanticC3.relationLimit",
                    DiagnosticSeverity.Warning,
                    null,
                    "Semantic C3 relation budget reached; additional wiring/collaboration facts were omitted.");
                return;
            }

            if (TryAdd(fact))
            {
                relationCount++;
            }
        }

        public void AddObservation(SemanticC3Fact fact) => TryAdd(fact);

        private bool TryAdd(SemanticC3Fact fact)
        {
            if (FactLimitReached || !ids.Add(fact.Id))
            {
                return false;
            }

            if (Items.Count >= options.MaxFacts)
            {
                FactLimitReached = true;
                diagnostics.Add(
                    "semanticC3.factLimit",
                    DiagnosticSeverity.Warning,
                    null,
                    "Semantic C3 fact budget reached; additional facts were omitted.");
                return false;
            }

            Items.Add(fact);
            return true;
        }
    }

    private sealed class DiagnosticAccumulator(int maxDiagnostics)
    {
        private readonly List<RepositoryDiagnostic> items = [];
        private readonly HashSet<string> keys = new(StringComparer.Ordinal);
        private bool limitRecorded;

        public ImmutableArray<RepositoryDiagnostic> Items =>
        [
            .. items
                .OrderBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.RelativePath, StringComparer.Ordinal)
                .ThenBy(item => item.Message, StringComparer.Ordinal),
        ];

        public void Add(
            string code,
            DiagnosticSeverity severity,
            string? relativePath,
            string message)
        {
            string key = code + "\n" + relativePath;
            if (!keys.Add(key))
            {
                return;
            }

            if (items.Count < maxDiagnostics - 1)
            {
                items.Add(new RepositoryDiagnostic(code, severity, relativePath, message));
                return;
            }

            if (!limitRecorded)
            {
                limitRecorded = true;
                items.Add(new RepositoryDiagnostic(
                    "semanticC3.diagnosticLimit",
                    DiagnosticSeverity.Warning,
                    null,
                    "Semantic C3 diagnostic budget reached; additional diagnostics were omitted."));
            }
        }
    }
}

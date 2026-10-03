using System.Collections.Immutable;
using System.Text;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Core.LikeC4;

/// <summary>A generated LikeC4 source file kept entirely in memory.</summary>
public sealed record LikeC4GeneratedFile(string FileName, string Content);

/// <summary>Pure, deterministic emission of a validated v1 architecture model into LikeC4 source files.</summary>
public static class LikeC4Emitter
{
    public const string SpecificationFileName = "specification.c4";
    public const string ModelFileName = "model.c4";
    public const string ViewsFileName = "views.c4";
    public const string C3ViewsFileName = "c3.views.c4";

    private const string RequiresReviewTag = "requires-review";

    public static ImmutableArray<LikeC4GeneratedFile> Emit(ArchitectureModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        ImmutableArray<ContractError> errors = ContractValidator.ValidateModel(model);
        if (!errors.IsEmpty)
        {
            throw new ContractValidationException(errors);
        }

        Dictionary<string, string> localIdentifiers = BuildLocalIdentifiers(model.Elements);
        Dictionary<string, string> references = BuildReferences(model.Elements, localIdentifiers);

        return
        [
            new LikeC4GeneratedFile(SpecificationFileName, EmitSpecification()),
            new LikeC4GeneratedFile(ModelFileName, EmitModel(model, localIdentifiers, references)),
            new LikeC4GeneratedFile(ViewsFileName, EmitViews(model, references)),
        ];
    }

    public static ImmutableArray<LikeC4GeneratedFile> EmitWithC3(
        ArchitectureModel baseModel,
        ArchitectureC3Model c3)
    {
        ArgumentNullException.ThrowIfNull(baseModel);
        ArgumentNullException.ThrowIfNull(c3);

        ImmutableArray<ContractError> c3Errors = ArchitectureC3Validator.Validate(c3);
        if (!c3Errors.IsEmpty)
        {
            throw new ContractValidationException(c3Errors);
        }

        ArchitectureC3Workspace workspace = new(
            c3.SchemaVersion,
            baseModel,
            [
                new ArchitectureC3Selection(
                    c3.SelectedContainerId,
                    c3.Components,
                    c3.Relations),
            ]);

        return EmitWithC3(workspace);
    }

    public static ImmutableArray<LikeC4GeneratedFile> EmitWithC3(
        ArchitectureC3Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        ImmutableArray<ContractError> errors =
            ArchitectureC3WorkspaceValidator.Validate(workspace);
        if (!errors.IsEmpty)
        {
            throw new ContractValidationException(errors);
        }

        if (workspace.Selections.IsEmpty)
        {
            return Emit(workspace.BaseModel);
        }

        for (int i = 0; i < workspace.Selections.Length; i++)
        {
            if (workspace.Selections[i].Components.IsEmpty)
            {
                throw new ContractValidationException(
                [
                    new ContractError(
                        "c3.insufficientEvidence",
                        "$.selections[" + i + "].components",
                        "Selected container has insufficient evidence for a C3 proposal."),
                ]);
            }
        }

        ArchitectureModel baseModel = workspace.BaseModel;
        ArchitectureC3Selection[] selections =
        [
            .. workspace.Selections
                .OrderBy(selection => selection.SelectedContainerId, StringComparer.Ordinal),
        ];

        Dictionary<string, string> localIdentifiers = BuildLocalIdentifiers(baseModel.Elements);
        Dictionary<string, string> references = BuildReferences(baseModel.Elements, localIdentifiers);
        Dictionary<string, string> componentIdentifiers = BuildComponentIdentifiers(
            selections,
            references);
        Dictionary<string, string> viewIdentifiers = BuildC3ViewIdentifiers(selections);

        return
        [
            new LikeC4GeneratedFile(SpecificationFileName, EmitSpecification(includeComponent: true)),
            new LikeC4GeneratedFile(
                ModelFileName,
                EmitModelWithC3(
                    baseModel,
                    selections,
                    localIdentifiers,
                    componentIdentifiers,
                    references)),
            new LikeC4GeneratedFile(ViewsFileName, EmitViews(baseModel, references)),
            new LikeC4GeneratedFile(
                C3ViewsFileName,
                EmitC3Views(
                    baseModel,
                    selections,
                    references,
                    viewIdentifiers)),
        ];
    }

    private static Dictionary<string, string> BuildComponentIdentifiers(
        ArchitectureC3Selection[] selections,
        Dictionary<string, string> references)
    {
        Dictionary<string, string> componentIdentifiers = new(StringComparer.Ordinal);
        HashSet<string> occupiedLocalIdentifiers = new(StringComparer.Ordinal);

        foreach (ArchitectureC3Selection selection in selections)
        {
            foreach (ArchitectureComponent component in selection.Components
                         .OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                string normalized = NormalizeIdentifier(component.Id);
                string collisionKey = selection.SelectedContainerId + "\u001f" + normalized;
                if (!occupiedLocalIdentifiers.Add(collisionKey))
                {
                    throw new ContractValidationException(
                    [
                        new ContractError(
                            "likec4.identifierCollision",
                            "$.selections",
                            "C3 component IDs normalize to the same LikeC4 identifier in one container."),
                    ]);
                }

                componentIdentifiers.Add(component.Id, normalized);
                references.Add(
                    component.Id,
                    references[selection.SelectedContainerId] + "." + normalized);
            }
        }

        return componentIdentifiers;
    }

    private static Dictionary<string, string> BuildC3ViewIdentifiers(
        ArchitectureC3Selection[] selections)
    {
        Dictionary<string, string> identifiers = new(StringComparer.Ordinal);
        if (selections.Length == 1)
        {
            identifiers.Add(selections[0].SelectedContainerId, "c3");
            return identifiers;
        }

        Dictionary<string, ArchitectureC3Selection[]> groups = selections
            .GroupBy(
                selection => "c3_" + NormalizeIdentifier(selection.SelectedContainerId),
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(selection => selection.SelectedContainerId, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        foreach ((string baseIdentifier, ArchitectureC3Selection[] group) in groups
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            foreach (ArchitectureC3Selection selection in group)
            {
                string identifier = group.Length == 1
                    ? baseIdentifier
                    : baseIdentifier + "_" + StableViewSuffix(selection.SelectedContainerId);
                identifiers.Add(selection.SelectedContainerId, identifier);
            }
        }

        return identifiers;
    }

    private static string StableViewSuffix(string containerId)
    {
        string stable = StableIds.ForElement(
            ArchitectureElementKind.Container,
            "c3-view|" + containerId);
        return stable["el_".Length..("el_".Length + 8)];
    }

    private static Dictionary<string, string> BuildLocalIdentifiers(
        ImmutableArray<ArchitectureElement> elements)
    {
        Dictionary<string, string> localIdentifiers = new(StringComparer.Ordinal);
        Dictionary<string, string> occupiedNames = new(StringComparer.Ordinal);

        foreach (ArchitectureElement element in elements.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            string normalized = NormalizeIdentifier(element.Id);
            string scope = element.ParentId ?? string.Empty;
            string collisionKey = scope + "\u001f" + normalized;

            if (occupiedNames.TryGetValue(collisionKey, out string? existingId))
            {
                throw new ContractValidationException(
                [
                    new ContractError(
                        "likec4.identifierCollision",
                        "$.elements",
                        "Architecture element IDs '" + existingId + "' and '" + element.Id
                            + "' normalize to the same LikeC4 identifier in one scope."),
                ]);
            }

            occupiedNames.Add(collisionKey, element.Id);
            localIdentifiers.Add(element.Id, normalized);
        }

        return localIdentifiers;
    }

    private static Dictionary<string, string> BuildReferences(
        ImmutableArray<ArchitectureElement> elements,
        Dictionary<string, string> localIdentifiers)
    {
        Dictionary<string, string> references = new(StringComparer.Ordinal);

        foreach (ArchitectureElement element in elements)
        {
            string reference = element.ParentId is null
                ? localIdentifiers[element.Id]
                : localIdentifiers[element.ParentId] + "." + localIdentifiers[element.Id];
            references.Add(element.Id, reference);
        }

        return references;
    }

    private static string NormalizeIdentifier(string id) => id.Replace('.', '_');

    private static string EmitSpecification(bool includeComponent = false)
    {
        StringBuilder builder = new();
        AppendLine(builder, 0, "specification {");
        AppendLine(builder, 1, "element actor {");
        AppendLine(builder, 2, "style {");
        AppendLine(builder, 3, "shape person");
        AppendLine(builder, 2, "}");
        AppendLine(builder, 1, "}");
        AppendLine(builder, 1, "element softwareSystem");
        AppendLine(builder, 1, "element container");
        if (includeComponent)
        {
            AppendLine(builder, 1, "element component");
        }

        AppendLine(builder, 1, "tag " + RequiresReviewTag + " {");
        AppendLine(builder, 2, "color amber");
        AppendLine(builder, 1, "}");
        AppendLine(builder, 0, "}");
        return builder.ToString();
    }

    private static string EmitModel(
        ArchitectureModel model,
        Dictionary<string, string> localIdentifiers,
        Dictionary<string, string> references)
    {
        StringBuilder builder = new();
        AppendLine(builder, 0, "model {");

        ArchitectureElement[] roots =
        [
            .. model.Elements
                .Where(element => element.ParentId is null)
                .OrderBy(element => element.Id, StringComparer.Ordinal),
        ];

        Dictionary<string, ArchitectureElement[]> children = model.Elements
            .Where(element => element.ParentId is not null)
            .GroupBy(element => element.ParentId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(element => element.Id, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        foreach (ArchitectureElement root in roots)
        {
            EmitElement(builder, root, localIdentifiers, children, 1);
        }

        if (roots.Length > 0 && model.Relations.Length > 0)
        {
            builder.Append('\n');
        }

        foreach (ArchitectureRelation relation in model.Relations.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            EmitRelation(builder, relation, references, 1);
        }

        AppendLine(builder, 0, "}");
        return builder.ToString();
    }

    private static void EmitElement(
        StringBuilder builder,
        ArchitectureElement element,
        Dictionary<string, string> localIdentifiers,
        Dictionary<string, ArchitectureElement[]> children,
        int indent)
    {
        string kind = element.Kind switch
        {
            ArchitectureElementKind.Actor => "actor",
            ArchitectureElementKind.SoftwareSystem => "softwareSystem",
            ArchitectureElementKind.Container => "container",
            _ => throw new ArgumentOutOfRangeException(nameof(element), "Unsupported architecture element kind."),
        };

        AppendLine(
            builder,
            indent,
            localIdentifiers[element.Id] + " = " + kind + " " + Quote(element.Name) + " {");

        if (element.Status == ReviewStatus.RequiresReview)
        {
            AppendLine(builder, indent + 1, "#" + RequiresReviewTag);
        }

        EmitMetadata(
            builder,
            element.Id,
            element.EvidenceIds,
            element.Status,
            element.ReviewReason,
            indent + 1);

        if (children.TryGetValue(element.Id, out ArchitectureElement[]? nested))
        {
            builder.Append('\n');
            foreach (ArchitectureElement child in nested)
            {
                EmitElement(builder, child, localIdentifiers, children, indent + 1);
            }
        }

        AppendLine(builder, indent, "}");
    }

    private static void EmitRelation(
        StringBuilder builder,
        ArchitectureRelation relation,
        Dictionary<string, string> references,
        int indent)
    {
        AppendLine(
            builder,
            indent,
            references[relation.SourceId] + " -> " + references[relation.DestinationId]
                + " " + Quote(relation.Description) + " {");

        if (relation.Status == ReviewStatus.RequiresReview)
        {
            AppendLine(builder, indent + 1, "#" + RequiresReviewTag);
        }

        EmitMetadata(
            builder,
            relation.Id,
            relation.EvidenceIds,
            relation.Status,
            relation.ReviewReason,
            indent + 1);

        AppendLine(builder, indent, "}");
    }

    private static void EmitMetadata(
        StringBuilder builder,
        string architectureId,
        ImmutableArray<string> evidenceIds,
        ReviewStatus status,
        string? reviewReason,
        int indent)
    {
        AppendLine(builder, indent, "metadata {");
        AppendLine(builder, indent + 1, "architectureId " + Quote(architectureId));

        if (!evidenceIds.IsEmpty)
        {
            string values = string.Join(
                ", ",
                evidenceIds
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .Select(Quote));
            AppendLine(builder, indent + 1, "evidenceIds [" + values + "]");
        }

        AppendLine(
            builder,
            indent + 1,
            "reviewStatus " + Quote(
                status == ReviewStatus.Confirmed
                    ? "confirmed"
                    : "requiresReview"));

        if (reviewReason is not null)
        {
            AppendLine(builder, indent + 1, "reviewReason " + Quote(reviewReason));
        }

        AppendLine(builder, indent, "}");
    }

    private static string EmitModelWithC3(
        ArchitectureModel model,
        ArchitectureC3Selection[] selections,
        Dictionary<string, string> localIdentifiers,
        Dictionary<string, string> componentIdentifiers,
        Dictionary<string, string> references)
    {
        StringBuilder builder = new();
        AppendLine(builder, 0, "model {");

        ArchitectureElement[] roots =
        [
            .. model.Elements
                .Where(element => element.ParentId is null)
                .OrderBy(element => element.Id, StringComparer.Ordinal),
        ];

        Dictionary<string, ArchitectureElement[]> children = model.Elements
            .Where(element => element.ParentId is not null)
            .GroupBy(element => element.ParentId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(element => element.Id, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        Dictionary<string, ArchitectureC3Selection> selectionsByContainer = selections
            .ToDictionary(
                selection => selection.SelectedContainerId,
                StringComparer.Ordinal);

        foreach (ArchitectureElement root in roots)
        {
            EmitElementWithC3(
                builder,
                root,
                selectionsByContainer,
                localIdentifiers,
                componentIdentifiers,
                children,
                1);
        }

        ArchitectureComponentRelation[] componentRelations =
        [
            .. selections
                .SelectMany(selection => selection.Relations)
                .OrderBy(relation => relation.Id, StringComparer.Ordinal),
        ];

        if ((roots.Length > 0 && model.Relations.Length > 0) ||
            componentRelations.Length > 0)
        {
            builder.Append('\n');
        }

        foreach (ArchitectureRelation relation in model.Relations
                     .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            EmitRelation(builder, relation, references, 1);
        }

        foreach (ArchitectureComponentRelation relation in componentRelations)
        {
            AppendLine(
                builder,
                1,
                references[relation.SourceId] + " -> " + references[relation.DestinationId]
                    + " " + Quote(relation.Description) + " {");
            if (relation.Status == ReviewStatus.RequiresReview)
            {
                AppendLine(builder, 2, "#" + RequiresReviewTag);
            }

            EmitMetadata(
                builder,
                relation.Id,
                relation.EvidenceIds,
                relation.Status,
                relation.ReviewReason,
                2);
            AppendLine(builder, 1, "}");
        }

        AppendLine(builder, 0, "}");
        return builder.ToString();
    }

    private static void EmitElementWithC3(
        StringBuilder builder,
        ArchitectureElement element,
        Dictionary<string, ArchitectureC3Selection> selectionsByContainer,
        Dictionary<string, string> localIdentifiers,
        Dictionary<string, string> componentIdentifiers,
        Dictionary<string, ArchitectureElement[]> children,
        int indent)
    {
        string kind = element.Kind switch
        {
            ArchitectureElementKind.Actor => "actor",
            ArchitectureElementKind.SoftwareSystem => "softwareSystem",
            ArchitectureElementKind.Container => "container",
            _ => throw new ArgumentOutOfRangeException(nameof(element), "Unsupported architecture element kind."),
        };

        AppendLine(
            builder,
            indent,
            localIdentifiers[element.Id] + " = " + kind + " " + Quote(element.Name) + " {");

        if (element.Status == ReviewStatus.RequiresReview)
        {
            AppendLine(builder, indent + 1, "#" + RequiresReviewTag);
        }

        EmitMetadata(
            builder,
            element.Id,
            element.EvidenceIds,
            element.Status,
            element.ReviewReason,
            indent + 1);

        if (selectionsByContainer.TryGetValue(
                element.Id,
                out ArchitectureC3Selection? selection))
        {
            builder.Append('\n');
            foreach (ArchitectureComponent component in selection.Components
                         .OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                AppendLine(
                    builder,
                    indent + 1,
                    componentIdentifiers[component.Id] + " = component " + Quote(component.Name) + " {");
                if (component.Status == ReviewStatus.RequiresReview)
                {
                    AppendLine(builder, indent + 2, "#" + RequiresReviewTag);
                }

                AppendLine(
                    builder,
                    indent + 2,
                    "description " + Quote(component.Responsibility));
                EmitMetadata(
                    builder,
                    component.Id,
                    component.EvidenceIds,
                    component.Status,
                    component.ReviewReason,
                    indent + 2);
                AppendLine(builder, indent + 1, "}");
            }
        }

        if (children.TryGetValue(element.Id, out ArchitectureElement[]? nested))
        {
            builder.Append('\n');
            foreach (ArchitectureElement child in nested)
            {
                EmitElementWithC3(
                    builder,
                    child,
                    selectionsByContainer,
                    localIdentifiers,
                    componentIdentifiers,
                    children,
                    indent + 1);
            }
        }

        AppendLine(builder, indent, "}");
    }

    private static string EmitC3Views(
        ArchitectureModel model,
        ArchitectureC3Selection[] selections,
        Dictionary<string, string> references,
        Dictionary<string, string> viewIdentifiers)
    {
        StringBuilder builder = new();
        Dictionary<string, ArchitectureElement> elements = model.Elements
            .ToDictionary(element => element.Id, StringComparer.Ordinal);

        AppendLine(builder, 0, "views {");

        foreach (ArchitectureC3Selection selection in selections)
        {
            ArchitectureElement selected = elements[selection.SelectedContainerId];
            AppendLine(
                builder,
                1,
                "view " + viewIdentifiers[selection.SelectedContainerId] + " {");
            AppendLine(builder, 2, "title " + Quote("C3 - " + selected.Name));
            AppendLine(builder, 2, "include " + references[selected.Id]);
            foreach (ArchitectureComponent component in selection.Components
                         .OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                AppendLine(builder, 2, "include " + references[component.Id]);
            }

            AppendLine(builder, 1, "}");
        }

        AppendLine(builder, 0, "}");
        return builder.ToString();
    }

    private static string EmitViews(
        ArchitectureModel model,
        Dictionary<string, string> references)
    {
        StringBuilder builder = new();
        string viewName = model.Level == ArchitectureLevel.C1 ? "c1" : "c2";
        string title = model.Level == ArchitectureLevel.C1
            ? "C1 - System Context"
            : "C2 - Containers";

        AppendLine(builder, 0, "views {");
        AppendLine(builder, 1, "view " + viewName + " {");
        AppendLine(builder, 2, "title " + Quote(title));

        foreach (ArchitectureElement element in model.Elements
                     .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            AppendLine(builder, 2, "include " + references[element.Id]);
        }

        AppendLine(builder, 1, "}");
        AppendLine(builder, 0, "}");
        return builder.ToString();
    }

    private static string Quote(string value)
    {
        StringBuilder builder = new(value.Length + 2);
        builder.Append('"');

        string normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        foreach (char character in normalized)
        {
            switch (character)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                case '\t':
                    builder.Append(character);
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        throw new ContractValidationException(
                        [
                            new ContractError(
                                "likec4.textControlCharacter",
                                "$",
                                "LikeC4 text contains an unsupported control character."),
                        ]);
                    }

                    builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static void AppendLine(
        StringBuilder builder,
        int indent,
        string text)
    {
        builder.Append(' ', indent * 2);
        builder.Append(text);
        builder.Append('\n');
    }
}

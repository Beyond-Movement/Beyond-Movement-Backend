using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BeyondMovement.Api.OpenApi;

/// <summary>
/// Declares the bearer scheme and marks which operations require it.
/// <para>
/// The generator does not infer this from the authorization pipeline, so without it the
/// contract would show every endpoint as open — and the Flutter client generated from that
/// contract would not attach the token.
/// </para>
/// </summary>
public sealed class BearerSecurityTransformer : IOpenApiDocumentTransformer
{
    private const string SchemeName = "bearerAuth";

    public Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Paste the accessToken returned by POST /api/v1/auth/login."
        };

        // Deny by default in the pipeline, so deny by default in the contract too:
        // every operation requires the token unless its endpoint opted out.
        foreach (var (_, pathItem) in document.Paths)
        {
            foreach (var (_, operation) in pathItem.Operations ?? [])
            {
                operation.Security =
                [
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(SchemeName, document)] = []
                    }
                ];
            }
        }

        RemoveRequirementFromAnonymousOperations(document, context);

        return Task.CompletedTask;
    }

    private static void RemoveRequirementFromAnonymousOperations(
        OpenApiDocument document, OpenApiDocumentTransformerContext context)
    {
        foreach (var description in context.DescriptionGroups.SelectMany(g => g.Items))
        {
            var isAnonymous = description.ActionDescriptor.EndpointMetadata
                .OfType<IAllowAnonymous>()
                .Any();

            if (!isAnonymous)
                continue;

            var path = "/" + description.RelativePath?.TrimEnd('/');

            if (description.HttpMethod is null)
                continue;

            // Operations are keyed by System.Net.Http.HttpMethod, not an enum.
            var method = HttpMethod.Parse(description.HttpMethod);

            if (document.Paths.TryGetValue(path, out var pathItem) &&
                pathItem.Operations?.TryGetValue(method, out var operation) == true)
            {
                operation.Security = null;
            }
        }
    }
}

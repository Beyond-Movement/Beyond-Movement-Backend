using System.Text.Json.Nodes;
using BeyondMovement.Modules.Identity.Contracts;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BeyondMovement.Api.OpenApi;

/// <summary>
/// Cleans up two things the generator produces that make the contract awkward to consume.
/// <list type="number">
/// <item>
/// Numbers are emitted as <c>type: [integer, string]</c> with a numeric pattern, because
/// System.Text.Json can read a number written as a string. The API only ever writes real
/// numbers, so the union forces client generators into an unusable "int or String" type.
/// Collapsed back to a plain integer.
/// </item>
/// <item>
/// <c>errorCode</c> is typed as a bare string. The permitted values are listed instead, so
/// the generated client gets a checkable set rather than free text.
/// </item>
/// </list>
/// </summary>
public sealed class SchemaNormalizingTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        CollapseNumericUnion(schema);

        // Property sub-schemas are not always visited on their own, so walk them too.
        foreach (var property in schema.Properties?.Values.OfType<OpenApiSchema>() ?? [])
            CollapseNumericUnion(property);

        if (context.JsonTypeInfo.Type == typeof(ApiProblemDetails))
            DeclareErrorCodeValues(schema);

        return Task.CompletedTask;
    }

    private static void CollapseNumericUnion(OpenApiSchema schema)
    {
        if (schema.Type is not { } type || !type.HasFlag(JsonSchemaType.String))
            return;

        // Preserve nullability; drop only the spurious string half.
        var nullFlag = type & JsonSchemaType.Null;

        if (type.HasFlag(JsonSchemaType.Integer))
        {
            schema.Type = JsonSchemaType.Integer | nullFlag;
            schema.Pattern = null;
        }
        else if (type.HasFlag(JsonSchemaType.Number))
        {
            schema.Type = JsonSchemaType.Number | nullFlag;
            schema.Pattern = null;
        }
    }

    private static void DeclareErrorCodeValues(OpenApiSchema schema)
    {
        if (schema.Properties?.TryGetValue("errorCode", out var errorCode) != true)
            return;

        if (errorCode is OpenApiSchema property)
            property.Enum = [.. ApiErrorCodes.All.Select(code => JsonValue.Create(code)! as JsonNode)];
    }
}

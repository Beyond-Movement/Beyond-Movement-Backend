using System.Text.Json.Nodes;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Packages;
using BeyondMovement.Modules.Packages.Contracts;
using BeyondMovement.Modules.Packages.Domain;
using BeyondMovement.Modules.Scheduling;
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
/// <item>
/// An enum used nullably anywhere gets <c>null</c> folded into the single shared component
/// schema, so <c>Gender</c> would advertise null as a legal value in the Complete Profile
/// request that requires it. The null member is dropped and the type stated as string;
/// per-property nullability is already carried by the property not being required.
/// </item>
/// </list>
/// </summary>
public sealed class SchemaNormalizingTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        CollapseNumericUnion(schema);
        DropNullFromEnum(schema);

        // Property sub-schemas are not always visited on their own, so walk them too.
        foreach (var property in schema.Properties?.Values.OfType<OpenApiSchema>() ?? [])
        {
            CollapseNumericUnion(property);
            DropNullFromEnum(property);
        }

        if (context.JsonTypeInfo.Type == typeof(ApiProblemDetails))
            DeclareErrorCodeValues(schema);

        DeclareLimits(schema, context.JsonTypeInfo.Type);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes the validation limits into the schema itself rather than leaving them described in
    /// prose, so a generated client can reject a bad value before it costs a round trip and a
    /// reader can see the bounds without parsing an English sentence.
    /// <para>
    /// They are stated here rather than read from the FluentValidation rules because there is no
    /// reliable mapping between the two — a rule expressed with <c>Must</c> has no schema
    /// equivalent at all. The cost is that these must be kept in step with the validators; the
    /// constants they are taken from are the same ones the validators use, which is what keeps
    /// them honest.
    /// </para>
    /// </summary>
    private static void DeclareLimits(OpenApiSchema schema, Type type)
    {
        if (type == typeof(SavePackageOptionRequest) || type == typeof(EditPackageOptionRequest))
        {
            Limit(schema, "name", s => s.MaxLength = PackageOption.MaxNameLength);

            Limit(schema, "sessions", s =>
            {
                s.Minimum = PackageOption.MinSessions.ToString();
                s.Maximum = PackageOption.MaxSessions.ToString();
            });

            Limit(schema, "defaultPriceMinor", s =>
            {
                s.Minimum = "0";
                s.Maximum = PackagePricing.MaxPriceMinor.ToString();
            });

            Limit(schema, "features", s =>
            {
                s.MinItems = PackageOption.MinFeatures;
                s.MaxItems = PackageOption.MaxFeatures;

                if (s.Items is OpenApiSchema item)
                    item.MaxLength = PackageOptionFeature.MaxTextLength;
            });

            Limit(schema, "version", s => s.Minimum = "1");
        }
        else if (type == typeof(SetCustomPriceRequest))
        {
            Limit(schema, "priceMinor", s =>
            {
                s.Minimum = "0";
                s.Maximum = PackagePricing.MaxPriceMinor.ToString();
            });
        }
    }

    private static void Limit(OpenApiSchema schema, string property, Action<OpenApiSchema> apply)
    {
        if (schema.Properties?.TryGetValue(property, out var found) == true
            && found is OpenApiSchema concrete)
        {
            apply(concrete);
        }
    }

    /// <summary>
    /// One component schema serves every use of an enum, so a single nullable use — an athlete
    /// who has not set a gender — would otherwise make null a legal value everywhere, including
    /// the request that requires one.
    /// </summary>
    private static void DropNullFromEnum(OpenApiSchema schema)
    {
        if (schema.Enum is not { Count: > 0 } values)
            return;

        var withoutNull = values
            .Where(value => value is not null && value.GetValueKind() != System.Text.Json.JsonValueKind.Null)
            .ToList();

        if (withoutNull.Count == values.Count)
            return;

        schema.Enum = withoutNull;
        schema.Type = JsonSchemaType.String;
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

        // Every module's codes, unioned here because the Api is the only project that can see
        // all of them - modules may not reference one another. A code missing from this list is
        // a code the generated client has no case for.
        string[] all = [.. ApiErrorCodes.All, .. PackageErrorCodes.All, .. SchedulingErrors.AllCodes];

        if (errorCode is OpenApiSchema property)
            property.Enum = [.. all.Order(StringComparer.Ordinal).Select(code => JsonValue.Create(code)! as JsonNode)];
    }
}

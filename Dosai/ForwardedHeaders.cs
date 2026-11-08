using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Depscan;

/// <summary>
///     Forwarded-header tracking, shared by the source walker and the assembly IL pass: request
///     values the ASP.NET Core ForwardedHeaders middleware rewrites (<c>Connection.RemoteIpAddress</c>
///     and <c>RemotePort</c> from <c>X-Forwarded-For</c>, <c>Request.Scheme</c> from
///     <c>X-Forwarded-Proto</c>, <c>Request.Host</c> from <c>X-Forwarded-Host</c>; verified against
///     <c>src/Middleware/HttpOverrides</c> in dotnet/aspnetcore), and direct reads of the
///     forwarded headers by literal name. Those values are sources of category
///     <see cref="Category" />; which of the three the middleware rewrites is in
///     <see cref="DataFlowPattern.TaintKinds" /> (<c>client-ip</c>, <c>host</c>, <c>scheme</c>),
///     and a direct header read carries <see cref="TaintKind" /> with the lower-cased header
///     name recorded in the source node's <see cref="DataFlowNode.Properties" /> under
///     <see cref="HeaderProperty" />. A <c>proxy-derived</c> value is spoofable only behind a
///     proxy that passes the headers through unchanged; nothing here decides whether a
///     deployment is vulnerable.
/// </summary>
internal static class ForwardedHeaders
{
    internal const string Category = "proxy-derived";
    internal const string TaintKind = "forwarded-header";
    internal const string HeaderProperty = "header";

    /// <summary>
    ///     The header set every analyzer in this vocabulary tags (chen's
    ///     <c>ForwardedHeaders.FORWARDED_HEADERS</c>, AppThreat/chen#312), lower-cased.
    /// </summary>
    internal static readonly HashSet<string> HeaderNames =
        new(StringComparer.Ordinal)
        {
            "forwarded", "x-forwarded-for", "x-forwarded-host", "x-forwarded-proto", "x-forwarded-port", "x-real-ip", "client-ip"
        };

    /// <summary>
    ///     The lower-cased header name a literal names, with the CGI server-variable spelling
    ///     (<c>HTTP_X_FORWARDED_FOR</c>) decoded to the same name; null when the literal is not a
    ///     forwarded header.
    /// </summary>
    internal static string? HeaderName(string literal)
    {
        var name = literal.ToLowerInvariant();
        if (name.StartsWith("http_", StringComparison.Ordinal) && name.Length > "http_".Length)
        {
            name = name["http_".Length..].Replace('_', '-');
        }

        return HeaderNames.Contains(name) ? name : null;
    }

    /// <summary>
    ///     The header a <c>Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersDefaults</c> name
    ///     property holds (<c>XForwardedForHeaderName</c> is <c>X-Forwarded-For</c>): the framework's
    ///     own spelling of the header names, which code passes in place of a literal. Only the
    ///     properties whose header is in <see cref="HeaderNames" /> map; the prefix and
    ///     X-Original-* names give null.
    /// </summary>
    internal static string? DefaultsHeaderName(string containingType, string propertyName)
    {
        if (containingType != "Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersDefaults")
        {
            return null;
        }

        return propertyName switch
        {
            "XForwardedForHeaderName" or "get_XForwardedForHeaderName" => "x-forwarded-for",
            "XForwardedHostHeaderName" or "get_XForwardedHostHeaderName" => "x-forwarded-host",
            "XForwardedProtoHeaderName" or "get_XForwardedProtoHeaderName" => "x-forwarded-proto",
            _ => null
        };
    }

    /// <summary>The direct-read source pattern for one header; the node records the name under <see cref="HeaderProperty" />.</summary>
    internal static DataFlowPattern SourcePattern(string headerName) => new()
    {
        Target = DataFlowPatternTarget.Source,
        Kind = DataFlowPatternKind.Method,
        Pattern = $"{HeaderProperty}:{headerName}",
        Category = Category,
        TaintKinds = [TaintKind, "http"],
        Description = $"Direct read of the '{headerName}' header, which a client can set through a proxy that forwards it unchanged"
    };

    /// <summary>The header name of a direct-read pattern minted by <see cref="SourcePattern" />, else null.</summary>
    internal static string? HeaderOf(DataFlowPattern pattern) =>
        pattern.Category == Category && pattern.TaintKinds.Contains(TaintKind, StringComparer.Ordinal) &&
        pattern.Pattern.StartsWith(HeaderProperty + ":", StringComparison.Ordinal)
            ? pattern.Pattern[(HeaderProperty.Length + 1)..]
            : null;

    /// <summary>
    ///     A direct forwarded-header read in source: an indexer or a TryGetValue/Get/GetValues
    ///     call on <c>Request.Headers</c> or <c>Request.ServerVariables</c> (both declared on an
    ///     <c>HttpRequest</c> type; <c>Response.Headers</c> is the same dictionary type and must
    ///     not match) whose argument is a string literal naming a forwarded header, or a
    ///     <c>ForwardedHeadersDefaults</c> name property. Matched on the resolved receiver member,
    ///     never on code text; any other non-literal name gives nothing.
    /// </summary>
    internal static DataFlowPattern? MatchSource(IOperation operation)
    {
        switch (operation)
        {
            case IPropertyReferenceOperation { Property.IsIndexer: true } indexer when IsRequestHeadersAccess(indexer.Instance):
                return ForwardedLiteralOf(indexer.ChildOperations) is { } indexerHeader ? SourcePattern(indexerHeader) : null;
            case IInvocationOperation invocation when IsRequestHeadersAccess(invocation.Instance) &&
                                                       invocation.TargetMethod.Name is "TryGetValue" or "Get" or "GetValues":
                return ForwardedLiteralOf(invocation.Arguments.Select(argument => argument.Value)) is { } invocationHeader ? SourcePattern(invocationHeader) : null;
            default:
                return null;
        }
    }

    /// <summary>
    ///     A <c>[FromHeader(Name = "X-Forwarded-For")]</c> parameter bound straight from a
    ///     forwarded header; the attribute must be the framework's own
    ///     (<c>Microsoft.AspNetCore.Mvc.FromHeaderAttribute</c>), named or constructor argument.
    /// </summary>
    internal static DataFlowPattern? FromHeaderParameterSource(IParameterSymbol parameter)
    {
        foreach (var attribute in parameter.GetAttributes())
        {
            if (attribute.AttributeClass is not { Name: "FromHeaderAttribute" or "FromHeader" } ||
                attribute.AttributeClass.ContainingNamespace?.ToDisplayString() != "Microsoft.AspNetCore.Mvc")
            {
                continue;
            }

            string? literal = null;
            foreach (var named in attribute.NamedArguments)
            {
                if (named.Key == "Name" && named.Value.Value is string name)
                {
                    literal = name;
                }
            }

            literal ??= attribute.ConstructorArguments.SelectMany(argument => argument.Values).Select(argument => argument.Value).OfType<string>().FirstOrDefault();
            if (literal is not null && HeaderName(literal) is { } headerName)
            {
                return SourcePattern(headerName);
            }
        }

        return null;
    }

    private static string? ForwardedLiteralOf(IEnumerable<IOperation> operations)
    {
        foreach (var operation in operations)
        {
            // An indexer's arguments appear as IArgumentOperation wrappers in ChildOperations
            // and bare values in Arguments; unwrap either before reading the constant.
            var candidate = operation is IArgumentOperation argument ? argument.Value : operation;
            if (candidate.ConstantValue is { HasValue: true, Value: string literal } && HeaderName(literal) is { } name)
            {
                return name;
            }

            if (candidate is IPropertyReferenceOperation { Property: var property } &&
                DefaultsHeaderName(Normalize(property.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) ?? string.Empty, property.Name) is { } defaultsName)
            {
                return defaultsName;
            }
        }

        return null;
    }

    /// <summary>The operation reads <c>Request.Headers</c>/<c>Request.ServerVariables</c> themselves, not a copy held in a local.</summary>
    private static bool IsRequestHeadersAccess(IOperation? instance)
    {
        while (instance is IConversionOperation conversion)
        {
            instance = conversion.Operand;
        }

        return instance switch
        {
            IPropertyReferenceOperation property => IsRequestHeadersMember(property.Property),
            IFieldReferenceOperation field => IsRequestHeadersMember(field.Field),
            _ => false
        };
    }

    private static bool IsRequestHeadersMember(ISymbol member) =>
        member.Name is "Headers" or "ServerVariables" &&
        Normalize(member.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) is "Microsoft.AspNetCore.Http.HttpRequest" or "System.Web.HttpRequest";

    /// <summary>The display form without the <c>global::</c> prefix Roslyn's fully-qualified format emits.</summary>
    private static string? Normalize(string? symbolName) =>
        string.IsNullOrEmpty(symbolName) ? symbolName : symbolName.Replace("global::", string.Empty, StringComparison.Ordinal);
}

using Microsoft.CodeAnalysis;

namespace Depscan.Frameworks;

/// <summary>
///     Conservative data-flow classification for service boundaries (CycloneDX services[].data[]).
///     Classifications derive from member names/types of the request/response DTO; the default is
///     "unknown" — never "public" — and every non-unknown classification names the triggering
///     member so a reviewer can audit it.
/// </summary>
public static class DataClassifier
{
    /// <summary>
    ///     Classification keywords, matched against whole word tokens of a member name rather than as
    ///     raw substrings.
    /// </summary>
    /// <remarks>
    ///     Two things went wrong before and both are corrected here. Matching was
    ///     <c>name.Contains(keyword)</c>, so <c>CompanyName</c> classified as financial (it contains
    ///     "pan") and <c>TermsAndConditions</c> as health (it contains "condition"). And several
    ///     keywords were inherently ambiguous even under exact-token matching — a standalone
    ///     <c>token</c> matches <c>ContinuationToken</c>, and <c>address</c> matches
    ///     <c>ServerAddress</c> — so those are replaced by their qualified forms. These entries drive
    ///     CycloneDX <c>services[].data[]</c>, where a false "credential" or "health" claim is far more
    ///     costly than a missed one.
    /// </remarks>
    private static readonly (string Classification, string[] Keywords)[] MemberRules =
    [
        ("credential", ["password", "passwd", "pwd", "secret", "apikey", "accesstoken", "refreshtoken", "bearertoken", "authtoken", "idtoken", "sastoken", "connectionstring", "privatekey", "clientsecret", "credential"]),
        ("pii", ["email", "emailaddress", "ssn", "socialsecurity", "dateofbirth", "dob", "phonenumber", "mobilenumber", "mobilephone", "homeaddress", "streetaddress", "billingaddress", "mailingaddress", "street", "zipcode", "postcode", "firstname", "lastname", "surname", "fullname", "middlename", "passport", "driverlicense", "geolocation", "latitude", "longitude", "ipaddress"]),
        ("financial", ["iban", "cardnumber", "creditcard", "debitcard", "cvv", "cvc", "pannumber", "accountnumber", "routingnumber", "accountbalance", "transactionamount"]),
        ("health", ["diagnosis", "patientid", "patientname", "medicalrecord", "medicalcondition", "prescription", "healthinsurance", "bloodtype"])
    ];

    private static readonly string[] CredentialTypes = ["System.Security.SecureString", "byte[]", "System.Byte[]"];

    /// <summary>
    ///     Offsets, within the underscore-stripped lowercase form of <paramref name="name" />, at which
    ///     a word token starts — derived from underscores and camel/Pascal case transitions. Offset 0
    ///     and the end of the string are always boundaries.
    /// </summary>
    private static List<int> TokenBoundaries(string name)
    {
        var boundaries = new List<int> { 0 };
        var offset = 0;
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '_')
            {
                continue;
            }

            // A capital that follows a lowercase or digit starts a new word (userId -> user|Id), as
            // does a capital followed by a lowercase inside a run of capitals (APIKey -> API|Key).
            if (offset > 0 && char.IsUpper(name[i]) &&
                (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                boundaries.Add(offset);
            }

            offset++;
        }

        boundaries.Add(offset);
        return boundaries;
    }

    /// <summary>
    ///     True when <paramref name="keyword" /> occupies whole word tokens of the member name: it must
    ///     both start and end on a token boundary. This is what stops "pan" from matching CompanyName
    ///     while still letting "cardnumber" match a Card/Number token pair.
    /// </summary>
    private static bool MatchesAtTokenBoundary(string normalized, List<int> boundaries, string keyword)
    {
        foreach (var start in boundaries)
        {
            var end = start + keyword.Length;
            if (end > normalized.Length)
            {
                continue;
            }

            if (boundaries.Contains(end) &&
                string.CompareOrdinal(normalized, start, keyword, 0, keyword.Length) == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Builds ServiceDataFlow entries for a request or response type with auditable
    ///     descriptions: one entry per distinct classification the members trigger, plus a single
    ///     "unknown" entry when nothing matched. Every non-unknown entry names its member.
    /// </summary>
    public static List<ServiceDataFlow> Describe(ITypeSymbol? type, string flow, string serviceId, string direction)
    {
        var classifications = ClassifyAll(type);
        if (classifications.Count == 0)
        {
            classifications.Add(("unknown", null));
        }

        var entries = new List<ServiceDataFlow>();
        foreach (var (classification, matchedMember) in classifications)
        {
            var entry = new ServiceDataFlow
            {
                Flow = flow,
                Classification = classification,
                Name = type?.ToDisplayString(),
                Confidence = classification == "unknown" ? ConfidenceTiers.Heuristic : ConfidenceTiers.Syntactic
            };
            if (direction == "inbound")
            {
                entry.Destination.Add(serviceId);
            }
            else
            {
                entry.Source.Add(serviceId);
            }

            entry.Description = classification switch
            {
                "unknown" when type is null => "type unresolved",
                "unknown" => "no classification keyword matched any member",
                _ => $"member '{matchedMember}' matched {classification} keywords"
            };
            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>Every distinct classification the type's members trigger, with one member name each.</summary>
    public static List<(string Classification, string? MatchedMember)> ClassifyAll(ITypeSymbol? type)
    {
        var results = new List<(string, string?)>();
        Collect(type, new HashSet<string>(StringComparer.Ordinal), new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default), results, depth: 0);
        return results;
    }

    private static void Collect(ITypeSymbol? type, HashSet<string> seen, HashSet<INamedTypeSymbol> visited, List<(string, string?)> results, int depth)
    {
        if (type is null or ITypeParameterSymbol || depth > 3)
        {
            return;
        }

        foreach (var member in type.GetMembers())
        {
            string? name = null;
            string? memberType = null;
            switch (member)
            {
                case IPropertySymbol property:
                    name = property.Name;
                    memberType = property.Type.ToDisplayString();
                    break;
                case IFieldSymbol field when field.DeclaredAccessibility is Accessibility.Public:
                    name = field.Name;
                    memberType = field.Type.ToDisplayString();
                    break;
            }

            if (name is null)
            {
                continue;
            }

            var normalized = name.Replace("_", string.Empty).ToLowerInvariant();
            var boundaries = TokenBoundaries(name);
            foreach (var (classification, keywords) in MemberRules)
            {
                if (!seen.Contains(classification) && keywords.Any(keyword => MatchesAtTokenBoundary(normalized, boundaries, keyword)))
                {
                    seen.Add(classification);
                    results.Add((classification, name));
                    break;
                }
            }

            if (memberType is not null && CredentialTypes.Any(credentialType => memberType.Contains(credentialType, StringComparison.Ordinal)) && normalized.Contains("key", StringComparison.Ordinal) && seen.Add("credential"))
            {
                results.Add(("credential", name));
            }
        }

        if (type is INamedTypeSymbol namedType && namedType.SpecialType == SpecialType.None && visited.Add(namedType))
        {
            foreach (var member in namedType.GetMembers())
            {
                var memberType = member switch
                {
                    IPropertySymbol property => property.Type,
                    IFieldSymbol field when field.DeclaredAccessibility is Accessibility.Public => field.Type,
                    _ => null
                };

                if (memberType is INamedTypeSymbol nested && nested.SpecialType == SpecialType.None && nested.TypeKind is TypeKind.Class or TypeKind.Struct && !visited.Contains(nested))
                {
                    Collect(nested, seen, visited, results, depth + 1);
                }
            }
        }
    }
}

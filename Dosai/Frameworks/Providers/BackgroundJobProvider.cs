using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     Background execution: IHostedService/BackgroundService workers, Hangfire recurring jobs and
///     dashboard, Quartz IJob implementations with cron triggers, and Coravel invocables. Also
///     flags unauthenticated Hangfire dashboards, a real finding in production apps.
/// </summary>
public sealed class BackgroundJobProvider : IFrameworkProvider
{
    public string Id => "background-jobs";

    public string DisplayName => "Background jobs (IHostedService, Hangfire, Quartz, Coravel)";

    public bool AppliesTo(FrameworkContext ctx) => ctx.CSharp is not null;

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        var hostedRegistrations = CollectHostedServiceRegistrations(ctx);
foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "BackgroundService", "IHostedService", "IJob", "IInvocable", "RecurringJob", "BackgroundJob", "Hangfire", "Quartz", "CronSchedule"))
            {
                continue;
            }

            var model = ctx.CSharp!.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            var rawUrls = ctx.RawUrlsFor(tree);

            foreach (var typeDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(typeDeclaration);
                var typeName = typeDeclaration.Identifier.Text;
                var namespaceName = typeDeclaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

                var isHosted = symbol is not null && (ProviderHelpers.DerivesFromAny(symbol, "BackgroundService") || ProviderHelpers.ImplementsAny(symbol, "IHostedService"));
                var isJob = symbol is not null && ProviderHelpers.ImplementsAny(symbol, "IJob");
                var isInvocable = symbol is not null && ProviderHelpers.ImplementsAny(symbol, "IInvocable");
                if (!isHosted && !isJob && !isInvocable)
                {
                    continue;
                }

                ctx.HandledTypeIds.Add($"{tree.FilePath}:{typeName}");
                var serviceKind = isHosted ? ServiceKinds.Scheduled : ServiceKinds.Scheduled;
                var lineSpan = typeDeclaration.GetLocation().GetLineSpan().StartLinePosition;
                // Sample suites reuse namespace+class names across projects; the directory keeps ids unique.
                var directoryGroup = Path.GetDirectoryName(CodeLocation.From(ctx.BasePath, tree.FilePath).Path)?.Replace(Path.DirectorySeparatorChar, '.');
                var serviceId = FrameworkIds.Service("background-jobs", string.IsNullOrWhiteSpace(directoryGroup) ? namespaceName : $"{namespaceName}.{directoryGroup}", typeName);
                var service = new ServiceComponent
                {
                    Id = serviceId,
                    Name = typeName,
                    Group = namespaceName,
                    ServiceKind = serviceKind,
                    Direction = ServiceDirections.Inbound,
                    Framework = "background-jobs",
                    Confidence = ConfidenceTiers.Semantic,
                    Location = CodeLocation.From(ctx.BasePath, tree.FilePath, lineSpan.Line + 1),
                    Evidence = new AnalysisEvidence
                    {
                        Kind = AnalysisEvidenceKind.FrameworkModel,
                        Source = "background-jobs",
                        Description = isHosted ? "Hosted service (IHostedService/BackgroundService)." : isJob ? "Quartz IJob implementation." : "Coravel IInvocable implementation.",
                        Confidence = ConfidenceTiers.Semantic,
                        FileName = Path.GetFileName(tree.FilePath),
                        LineNumber = lineSpan.Line + 1
                    }
                };
                service.Properties["registered"] = hostedRegistrations.Contains(typeName) ? "true" : "unknown";
                if (isJob && ProviderHelpers.AttributesOf(typeDeclaration.AttributeLists).Any(attribute => ProviderHelpers.IsNamed(attribute, "DisallowConcurrentExecution")))
                {
                    service.Properties["disallowConcurrentExecution"] = "true";
                }

                var cron = CronLiteralsNear(root, typeName);
                if (cron is not null)
                {
                    service.Properties["cron"] = cron;
                    service.Properties["schedule"] = HumanizeCron(cron);
                }

                results.Services.Add(service);

                foreach (var method in typeDeclaration.Members.OfType<MethodDeclarationSyntax>())
                {
                    var isExecutionMethod = method.Identifier.Text is "StartAsync" or "ExecuteAsync" or "StopAsync" or "Execute";
                    if (!isExecutionMethod)
                    {
                        continue;
                    }

                    var methodLineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
                    var methodSymbol = model.GetDeclaredSymbol(method);
                    var methodId = methodSymbol is null ? null : Depscan.Dosai.FormatMethodSignature(methodSymbol);
                    var operationId = FrameworkIds.Operation(serviceId, null, null, method.Identifier.Text);
                    service.Operations.Add(new ServiceOperation
                    {
                        Id = operationId,
                        Name = method.Identifier.Text,
                        MethodId = methodId,
                        Confidence = ConfidenceTiers.Semantic,
                        Location = CodeLocation.From(ctx.BasePath, tree.FilePath, methodLineSpan.Line + 1, methodLineSpan.Character + 1)
                    });
                    results.ApiEndpoints.Add(new ApiEndpoint
                    {
                        FilePath = CodeLocation.From(ctx.BasePath, tree.FilePath).Path,
                        FileName = Path.GetFileName(tree.FilePath),
                        Namespace = namespaceName,
                        ClassName = typeName,
                        MethodName = method.Identifier.Text,
                        EndpointKind = "ScheduledJob",
                        RoutingKind = "Attribute",
                        Framework = "background-jobs",
                        ServiceId = serviceId,
                        OperationId = operationId,
                        Confidence = ConfidenceTiers.Semantic,
                        LineNumber = methodLineSpan.Line + 1,
                        ColumnNumber = methodLineSpan.Character + 1,
                        RawUrls = rawUrls,
                        Route = typeName,
                        Evidence = new AnalysisEvidence
                        {
                            Kind = AnalysisEvidenceKind.FrameworkModel,
                            Source = "background-jobs",
                            Description = $"{(isHosted ? "Hosted service" : isJob ? "Quartz job" : "Coravel invocable")} entry method.",
                            Confidence = ConfidenceTiers.Semantic,
                            FileName = Path.GetFileName(tree.FilePath),
                            LineNumber = methodLineSpan.Line + 1
                        }
                    });
                    results.EntryPoints.Add(new EntryPoint
                    {
                        Id = $"ep:{operationId}",
                        Kind = isHosted ? "HostedService" : "ScheduledJob",
                        MethodId = methodId,
                        MethodName = method.Identifier.Text,
                        ClassName = typeName,
                        Namespace = namespaceName,
                        FileName = Path.GetFileName(tree.FilePath),
                        Path = CodeLocation.From(ctx.BasePath, tree.FilePath).Path,
                        LineNumber = methodLineSpan.Line + 1,
                        ColumnNumber = methodLineSpan.Character + 1
                    });
                }
            }

            // Hangfire registrations: RecurringJob.AddOrUpdate / BackgroundJob.Enqueue plus the dashboard mount.
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = ProviderHelpers.InvocationName(invocation);
                if (name is not ("AddOrUpdate" or "Enqueue" or "ContinueWith" or "Schedule"))
                {
                    continue;
                }

                var literals = ProviderHelpers.StringArguments(invocation);
                var cron = literals.FirstOrDefault(literal => literal.Contains(' ') && literal.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is >= 5 and <= 7);
                var jobId = literals.FirstOrDefault();
                var containingClass = invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                var className = containingClass?.Identifier.Text ?? Path.GetFileNameWithoutExtension(tree.FilePath);
                var namespaceName = containingClass?.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
                var serviceId = FrameworkIds.Service("background-jobs", namespaceName, $"{className}-hangfire-{name.ToLowerInvariant()}");
                if (results.Services.Any(existing => existing.Id == serviceId))
                {
                    continue;
                }

                var invocationLineSpan = invocation.GetLocation().GetLineSpan().StartLinePosition;
                var hangfireService = new ServiceComponent
                {
                    Id = serviceId,
                    Name = $"{className}.{name}",
                    Group = namespaceName,
                    ServiceKind = ServiceKinds.Scheduled,
                    Direction = ServiceDirections.Inbound,
                    Framework = "background-jobs",
                    Confidence = ConfidenceTiers.Syntactic,
                    Location = CodeLocation.From(ctx.BasePath, tree.FilePath, invocationLineSpan.Line + 1),
                    Evidence = new AnalysisEvidence
                    {
                        Kind = AnalysisEvidenceKind.FrameworkModel,
                        Source = "background-jobs",
                        Description = $"Hangfire {name} registration.",
                        Confidence = ConfidenceTiers.Syntactic,
                        FileName = Path.GetFileName(tree.FilePath),
                        LineNumber = invocationLineSpan.Line + 1
                    }
                };
                hangfireService.Properties["framework"] = "hangfire";
                hangfireService.Properties["jobId"] = jobId ?? "unknown";
                if (cron is not null && cron.Contains(' ') && cron.Split(' ').Length >= 5)
                {
                    hangfireService.Properties["cron"] = cron;
                    hangfireService.Properties["schedule"] = HumanizeCron(cron);
                }

                results.Services.Add(hangfireService);
                hangfireService.Operations.Add(new ServiceOperation
                {
                    Id = FrameworkIds.Operation(serviceId, null, null, name),
                    Name = name,
                    Confidence = ConfidenceTiers.Syntactic,
                    Location = CodeLocation.From(ctx.BasePath, tree.FilePath, invocationLineSpan.Line + 1)
                });
            }
        }

        AnalyzeHangfireDashboard(ctx, results);
    }

    private static HashSet<string> CollectHostedServiceRegistrations(FrameworkContext ctx)
    {
        var registrations = new HashSet<string>(StringComparer.Ordinal);
foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "BackgroundService", "IHostedService", "IJob", "IInvocable", "RecurringJob", "BackgroundJob", "Hangfire", "Quartz", "CronSchedule"))
            {
                continue;
            }

            foreach (var invocation in tree.GetCompilationUnitRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!ProviderHelpers.InvocationName(invocation).Equals("AddHostedService", StringComparison.Ordinal))
                {
                    continue;
                }

                var typeArgument = (invocation.Expression as MemberAccessExpressionSyntax)?.Name as GenericNameSyntax;
                var argument = typeArgument?.TypeArgumentList.Arguments.FirstOrDefault()?.ToString();
                if (argument is not null)
                {
                    registrations.Add(argument.Split('.').Last());
                }
            }
        }

        return registrations;
    }

    private static void AnalyzeHangfireDashboard(FrameworkContext ctx, FrameworkResults results)
    {
        foreach (var mount in ctx.MountPoints.Where(mount => mount.Kind == "hangfire-dashboard"))
        {
            var service = new ServiceComponent
            {
                Id = FrameworkIds.Service("background-jobs", null, "hangfire-dashboard"),
                Name = "Hangfire dashboard",
                ServiceKind = ServiceKinds.Other,
                Direction = ServiceDirections.Inbound,
                Framework = "background-jobs",
                Confidence = ConfidenceTiers.Syntactic,
                Endpoints = [mount.Path],
                Location = CodeLocation.From(ctx.BasePath, mount.FileName, mount.LineNumber),
                Evidence = new AnalysisEvidence
                {
                    Kind = AnalysisEvidenceKind.FrameworkModel,
                    Source = "background-jobs",
                    Description = "Hangfire dashboard mount.",
                    Confidence = ConfidenceTiers.Syntactic,
                    FileName = mount.FileName is null ? null : Path.GetFileName(mount.FileName),
                    LineNumber = mount.LineNumber
                }
            };

            // Dashboard authorization: check the registration line neighborhood for authorization
            // calls. Uses the cached tree text (matching the compiled source) instead of re-reading
            // the file from disk.
            var authorized = false;
            var mountTree = ctx.CSharpTrees.FirstOrDefault(tree => tree.FilePath == mount.FileName);
            if (mountTree is not null)
            {
                var lines = ctx.TextFor(mountTree).Split('\n');
                var from = Math.Max(0, mount.LineNumber - 1);
                var to = Math.Min(lines.Length - 1, mount.LineNumber + 3);
                for (var i = from; i <= to; i++)
                {
                    if (lines[i].Contains("WithAuthorization", StringComparison.Ordinal) || lines[i].Contains("RequireAuthorization", StringComparison.Ordinal) || lines[i].Contains("DashboardOptions", StringComparison.Ordinal) && lines[i].Contains("Authorization", StringComparison.Ordinal))
                    {
                        authorized = true;
                    }
                }
            }

            service.Properties["dashboardAuthorization"] = authorized ? "required" : "missing";
            if (!authorized)
            {
                service.Tags.Add("finding:unauthenticated-hangfire-dashboard");
            }

            results.Services.Add(service);
        }
    }

    /// <summary>The first cron-looking string literal (5-7 space-separated fields) near the type declaration.</summary>
    private static string? CronLiteralsNear(Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax root, string typeName)
    {
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (literal.Token.Value is not string value || !value.Contains(' '))
            {
                continue;
            }

            var fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length is >= 5 and <= 7 && fields.All(field => field.Length > 0 && field.All(c => char.IsLetterOrDigit(c) || "*,/?-".Contains(c))))
            {
                return value;
            }
        }

        return null;
    }

    internal static string HumanizeCron(string cron)
    {
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5)
        {
            return cron;
        }

        var (minute, hour, dayOfMonth, month, dayOfWeek) = (fields[0], fields[1], fields[2], fields[3], fields[4]);
        if (minute == "*" && hour == "*" && dayOfMonth == "*" && month == "*" && dayOfWeek == "*")
        {
            return "every minute";
        }

        if (minute != "*" && hour == "*" && dayOfMonth == "*" && month == "*" && dayOfWeek == "*")
        {
            return $"every hour at :{minute.PadLeft(2, '0')}";
        }

        if (minute != "*" && hour != "*" && dayOfMonth == "*" && month == "*" && dayOfWeek == "*")
        {
            return $"daily at {hour.PadLeft(2, '0')}:{minute.PadLeft(2, '0')}";
        }

        if (minute.StartsWith("*/") && hour == "*")
        {
            return $"every {minute[2..]} minutes";
        }

        if (hour.StartsWith("*/"))
        {
            return $"every {hour[2..]} hours";
        }

        return cron;
    }
}

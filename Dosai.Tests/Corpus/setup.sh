#!/usr/bin/env bash
# Clones and builds the Dosai corpus test corpus at pinned commits.
# Run once before executing CorpusTests; see README.md for what each app covers.
set -euo pipefail

# DOSAI_CORPUS_DIR picks the corpus directory (a leading ~ is expanded, as CorpusLocation does
# for the tests); unset or empty means ~/sandbox/dosai-corpus.
CORPUS_ROOT="${DOSAI_CORPUS_DIR:-${HOME}/sandbox/dosai-corpus}"
case "${CORPUS_ROOT}" in
    "~") CORPUS_ROOT="${HOME}" ;;
    "~/"*) CORPUS_ROOT="${HOME}/${CORPUS_ROOT#\~/}" ;;
esac
mkdir -p "${CORPUS_ROOT}"

clone_at_commit() {
    local repo_url="$1"
    local commit="$2"
    local target_dir="$3"

    if [ -d "${target_dir}/.git" ]; then
        echo "Already cloned: ${target_dir}"
        return
    fi

    git clone --quiet "${repo_url}" "${target_dir}"
    git -C "${target_dir}" checkout --quiet "${commit}"
}

echo "==> eShopOnWeb (ASP.NET Core MVC + minimal APIs + auth + configuration)"
clone_at_commit \
    https://github.com/dotnet-architecture/eShopOnWeb \
    4da8212117e87d808d4bbc7da6286fd2147ce606 \
    "${CORPUS_ROOT}/eShopOnWeb"
dotnet build "${CORPUS_ROOT}/eShopOnWeb/eShopOnWeb.sln" -v quiet

echo "==> practical-aspnetcore orleans-1 (Microsoft Orleans minimal hosting)"
clone_at_commit \
    https://github.com/dodyg/practical-aspnetcore \
    91fb02ba0ea97266c58c04fd0cadfdaa1b899022 \
    "${CORPUS_ROOT}/practical-aspnetcore"
dotnet build "${CORPUS_ROOT}/practical-aspnetcore/projects/orleans/orleans-1" -v quiet

echo "==> modular-monolith-with-ddd (net8.0 LTS app; TFM declared in src/Directory.Build.props)"
clone_at_commit \
    https://github.com/kgrzybek/modular-monolith-with-ddd \
    91c8ef24b4cb6ef558c95d8267fa07d68c7059f8 \
    "${CORPUS_ROOT}/modular-monolith-with-ddd"
dotnet build "${CORPUS_ROOT}/modular-monolith-with-ddd/src/API/CompanyName.MyMeetings.API/CompanyName.MyMeetings.API.csproj" -v quiet -p:NuGetAudit=false

echo "==> grpc-dotnet Grpc.Net.Client (multi-target library: net462;netstandard2.0;netstandard2.1;net8.0;net9.0;net10.0)"
clone_at_commit \
    https://github.com/grpc/grpc-dotnet \
    74de8cad36e5d4a987b44f78eb4ef9f2d60592f1 \
    "${CORPUS_ROOT}/grpc-dotnet"
dotnet build "${CORPUS_ROOT}/grpc-dotnet/src/Grpc.Net.Client/Grpc.Net.Client.csproj" -v quiet

echo "==> issue78-mailkit (issue #78 repro: a console app whose only package is MailKit, built into bin/)"
ISSUE78="${CORPUS_ROOT}/issue78-mailkit"
mkdir -p "${ISSUE78}"
cat > "${ISSUE78}/Repro.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="MailKit" Version="4.17.0" />
  </ItemGroup>

</Project>
CSPROJ
cat > "${ISSUE78}/Program.cs" <<'PROGRAM'
using MailKit.Net.Smtp;
using MimeKit;

var message = new MimeMessage();
message.From.Add(MailboxAddress.Parse("sender@example.com"));
message.To.Add(MailboxAddress.Parse(args.Length > 0 ? args[0] : "someone@example.com"));
message.Subject = "Code " + new Random().Next(1000, 9999);
message.Body = new TextPart("plain") { Text = "Hello" };

using var client = new SmtpClient();
client.Connect("localhost", 25);
client.Send(message);
client.Disconnect(true);
PROGRAM
dotnet build "${ISSUE78}/Repro.csproj" -c Release -v quiet

echo "==> issue79-newtonsoft (issue #79 repro: a console app built into out/, locally and with a /_/ path map)"
for variant in local pathmap; do
    ISSUE79="${CORPUS_ROOT}/issue79-newtonsoft/${variant}"
    mkdir -p "${ISSUE79}"
    cat > "${ISSUE79}/Repro.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>

</Project>
CSPROJ
    cat > "${ISSUE79}/Program.cs" <<'PROGRAM'
using Newtonsoft.Json;

var json = JsonConvert.SerializeObject(new { Name = args.Length > 0 ? args[0] : "world" });
Console.WriteLine(json);
PROGRAM
    if [ "${variant}" = pathmap ]; then
        # What ContinuousIntegrationBuild does for a git checkout: the PDB names /_/Program.cs.
        dotnet build "${ISSUE79}/Repro.csproj" -c Release -o "${ISSUE79}/out" "-p:PathMap=${ISSUE79}/=/_/" -v quiet
    else
        dotnet build "${ISSUE79}/Repro.csproj" -c Release -o "${ISSUE79}/out" -v quiet
    fi
done

echo "==> issue107-nodatime (issue #107 repro: an app and a class library, NodaTime copied into App/bin)"
ISSUE107="${CORPUS_ROOT}/issue107-nodatime"
if [ ! -d "${ISSUE107}/App" ]; then
    mkdir -p "${ISSUE107}"
    (
        cd "${ISSUE107}"
        dotnet new classlib -n Lib -o Lib > /dev/null
        dotnet new console -n App -o App > /dev/null
        sed -i.bak "s|<TargetFramework>[^<]*</TargetFramework>|<TargetFramework>net8.0</TargetFramework>|" Lib/Lib.csproj App/App.csproj
        dotnet add App/App.csproj reference Lib/Lib.csproj > /dev/null
        dotnet add App/App.csproj package NodaTime --version 3.2.0 > /dev/null
        cat > App/Program.cs <<'PROGRAM'
using NodaTime;

Console.WriteLine(SystemClock.Instance.GetCurrentInstant());
PROGRAM
    )
fi
dotnet build "${ISSUE107}/App/App.csproj" -c Release -v quiet

echo "==> issue107-mailkit (issue #107 dataflows repro: a minimal API with MailKit)"
ISSUE107_MAIL="${CORPUS_ROOT}/issue107-mailkit"
if [ ! -d "${ISSUE107_MAIL}/Api" ]; then
    mkdir -p "${ISSUE107_MAIL}"
    (
        cd "${ISSUE107_MAIL}"
        dotnet new web -n Api -o Api > /dev/null
        sed -i.bak "s|<TargetFramework>[^<]*</TargetFramework>|<TargetFramework>net8.0</TargetFramework>|" Api/Api.csproj
        dotnet add Api/Api.csproj package MailKit --version 4.8.0 > /dev/null
        cat > Api/Program.cs <<'PROGRAM'
using MailKit.Net.Smtp;
using MimeKit;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapPost("/send", (SendRequest request) =>
{
    var message = new MimeMessage();
    message.From.Add(MailboxAddress.Parse(request.From));
    message.To.Add(MailboxAddress.Parse(request.To));

    using var client = new SmtpClient();
    client.Connect(request.Host, 25);
    client.Send(message);
    return Results.Ok();
});

app.Run();

public record SendRequest(string From, string To, string Host);
PROGRAM
    )
fi
dotnet build "${ISSUE107_MAIL}/Api/Api.csproj" -c Release -v quiet

echo "==> issue111-core (issue #111 repro: a class library implementing NodaTime's IClock, built under an app named App and one named Web)"
for app in App Web; do
    ISSUE111="${CORPUS_ROOT}/issue111-core/${app}"
    if [ ! -d "${ISSUE111}/Core" ]; then
        mkdir -p "${ISSUE111}"
        (
            cd "${ISSUE111}"
            dotnet new classlib -n Core -o Core > /dev/null
            dotnet new console -n "${app}" -o "${app}" > /dev/null
            sed -i.bak "s|<TargetFramework>[^<]*</TargetFramework>|<TargetFramework>net8.0</TargetFramework>|" Core/Core.csproj "${app}/${app}.csproj"
            dotnet add Core/Core.csproj package NodaTime --version 3.2.0 > /dev/null
            dotnet add "${app}/${app}.csproj" reference Core/Core.csproj > /dev/null
            rm Core/Class1.cs
            cat > Core/FixedClock.cs <<'PROGRAM'
using NodaTime;

namespace Core;

public class FixedClock(Instant now) : IClock
{
    public Instant GetCurrentInstant() => now;
}
PROGRAM
            cat > Core/Greeting.cs <<'PROGRAM'
namespace Core;

public static class Greeting
{
    public static string Hello() => "hello";
}
PROGRAM
            cat > "${app}/Program.cs" <<'PROGRAM'
Console.WriteLine(new Core.FixedClock(NodaTime.Instant.FromUnixTimeSeconds(0)).GetCurrentInstant());
Console.WriteLine(Core.Greeting.Hello());
PROGRAM
        )
    fi
    dotnet build "${ISSUE111}/${app}/${app}.csproj" -v quiet -p:NuGetAudit=false
done

echo "==> issue110-serilog (issue #110 repro: a Web SDK app and a class library that logs through Serilog)"
ISSUE110="${CORPUS_ROOT}/issue110-serilog"
if [ ! -d "${ISSUE110}/Lib" ]; then
    mkdir -p "${ISSUE110}"
    (
        cd "${ISSUE110}"
        dotnet new web -n App -o App > /dev/null
        dotnet new classlib -n Lib -o Lib > /dev/null
        dotnet add Lib/Lib.csproj package Serilog --version 4.0.2 > /dev/null
        dotnet add App/App.csproj reference Lib/Lib.csproj > /dev/null
        rm Lib/Class1.cs
        cat > Lib/Greeter.cs <<'PROGRAM'
using Serilog;

namespace Lib;

public static class Greeter
{
    public static void Greet(ILogger logger, string name)
    {
        logger.Information("Hello {Name}", name);
    }
}
PROGRAM
    )
fi
dotnet restore "${ISSUE110}/App/App.csproj" -v quiet -p:NuGetAudit=false

echo "==> issue98 (issue #98 repros: calls in .razor and .cshtml files; built with $(dotnet --version))"
ISSUE98="${CORPUS_ROOT}/issue98"
mkdir -p "${ISSUE98}"

# The issue's CountParser.cs: a call in a .cs file, which the source analysis already lists once.
write_issue98_count_parser() {
    local project="$1" namespace="$2"
    mkdir -p "${project}/Services"
    cat > "${project}/Services/CountParser.cs" <<PROGRAM
using Newtonsoft.Json;

namespace ${namespace}.Services;

public static class CountParser
{
    public static Dictionary<string, int>? Parse(string json) =>
        JsonConvert.DeserializeObject<Dictionary<string, int>>(json);
}
PROGRAM
}

if [ ! -d "${ISSUE98}/blazor" ]; then
    (
        cd "${ISSUE98}"
        dotnet new blazor -n RazorCalls -o blazor > /dev/null
        dotnet add blazor/RazorCalls.csproj package Newtonsoft.Json --version 13.0.3 > /dev/null
        # The exact Counter.razor of the issue: one call in markup, one in @code.
        cat > blazor/Components/Pages/Counter.razor <<'RAZOR'
@page "/counter"
@rendermode InteractiveServer
@using Newtonsoft.Json

<PageTitle>Counter</PageTitle>

<h1>Counter</h1>

<p role="status">Current count: @currentCount</p>
<p>As JSON: @JsonConvert.ToString(currentCount)</p>

<button class="btn btn-primary" @onclick="IncrementCount">Click me</button>

@code {
    private int currentCount = 0;
    private string snapshot = "";

    private void IncrementCount()
    {
        currentCount++;
        snapshot = JsonConvert.SerializeObject(new { currentCount });
    }
}
RAZOR
        write_issue98_count_parser blazor RazorCalls
    )
fi
dotnet build "${ISSUE98}/blazor/RazorCalls.csproj" -v quiet -p:NuGetAudit=false

if [ ! -d "${ISSUE98}/webapp" ]; then
    (
        cd "${ISSUE98}"
        dotnet new webapp -n RazorCalls -o webapp > /dev/null
        dotnet add webapp/RazorCalls.csproj package Newtonsoft.Json --version 13.0.3 > /dev/null
        # One call in the markup and one in an @functions block.
        cat > webapp/Pages/Privacy.cshtml <<'RAZOR'
@page
@using Newtonsoft.Json
@model PrivacyModel
@{
    ViewData["Title"] = "Privacy Policy";
}
<h1>@ViewData["Title"]</h1>

<p>Use this page to detail your site's privacy policy.</p>
<p>As JSON: @JsonConvert.ToString(visits)</p>

@functions {
    private int visits = 3;

    private string Snapshot()
    {
        return JsonConvert.SerializeObject(new { visits });
    }
}
RAZOR
        write_issue98_count_parser webapp RazorCalls
    )
fi
dotnet build "${ISSUE98}/webapp/RazorCalls.csproj" -v quiet -p:NuGetAudit=false

if [ ! -d "${ISSUE98}/mvc" ]; then
    (
        cd "${ISSUE98}"
        dotnet new mvc -n RazorCalls -o mvc > /dev/null
        dotnet add mvc/RazorCalls.csproj package Newtonsoft.Json --version 13.0.3 > /dev/null
        cat > mvc/Views/Home/Privacy.cshtml <<'RAZOR'
@using Newtonsoft.Json
@{
    ViewData["Title"] = "Privacy Policy";
}
<h1>@ViewData["Title"]</h1>

<p>Use this page to detail your site's privacy policy.</p>
<p>As JSON: @JsonConvert.ToString(ViewData["Title"])</p>
<p>Again: @JsonConvert.SerializeObject(new { Title = ViewData["Title"] })</p>
RAZOR
        write_issue98_count_parser mvc RazorCalls
    )
fi
dotnet build "${ISSUE98}/mvc/RazorCalls.csproj" -v quiet -p:NuGetAudit=false

if [ ! -d "${ISSUE98}/blazorwasm" ]; then
    (
        cd "${ISSUE98}"
        dotnet new blazorwasm -n RazorCalls -o blazorwasm > /dev/null
        dotnet add blazorwasm/RazorCalls.csproj package Newtonsoft.Json --version 13.0.3 > /dev/null
        cat > blazorwasm/Pages/Counter.razor <<'RAZOR'
@page "/counter"
@using Newtonsoft.Json

<PageTitle>Counter</PageTitle>

<h1>Counter</h1>

<p role="status">Current count: @currentCount</p>
<p>As JSON: @JsonConvert.ToString(currentCount)</p>

<button class="btn btn-primary" @onclick="IncrementCount">Click me</button>

@code {
    private int currentCount = 0;
    private string snapshot = "";

    private void IncrementCount()
    {
        currentCount++;
        snapshot = JsonConvert.SerializeObject(new { currentCount });
    }
}
RAZOR
        write_issue98_count_parser blazorwasm RazorCalls
    )
fi
dotnet build "${ISSUE98}/blazorwasm/RazorCalls.csproj" -v quiet -p:NuGetAudit=false

# A Razor component library referenced by an app (the library's assembly is copied into the app's
# bin/, issue #83), and a component whose partial class has a .razor.cs code-behind.
if [ ! -d "${ISSUE98}/components/Host" ]; then
    mkdir -p "${ISSUE98}/components"
    (
        cd "${ISSUE98}/components"
        dotnet new razorclasslib -n Widgets -o Widgets > /dev/null
        dotnet new blazor -n Host -o Host > /dev/null
        dotnet add Widgets/Widgets.csproj package Newtonsoft.Json --version 13.0.3 > /dev/null
        dotnet add Host/Host.csproj package Newtonsoft.Json --version 13.0.3 > /dev/null
        dotnet add Host/Host.csproj reference Widgets/Widgets.csproj > /dev/null
        find Widgets -maxdepth 1 \( -name '*.razor' -o -name '*.razor.css' \) -delete
        cat > Widgets/Badge.razor <<'RAZOR'
@using Newtonsoft.Json

<span class="badge">@JsonConvert.ToString(Count)</span>

@code {
    [Parameter] public int Count { get; set; }

    public string Describe() => JsonConvert.SerializeObject(new { Count });
}
RAZOR
        cat > Host/Components/Pages/Behind.razor <<'RAZOR'
@page "/behind"
@using Newtonsoft.Json
@using Widgets

<h1>Code behind</h1>

<Badge Count="@total" />
<p>@JsonConvert.ToString(total)</p>
RAZOR
        cat > Host/Components/Pages/Behind.razor.cs <<'PROGRAM'
using Newtonsoft.Json;

namespace Host.Components.Pages;

public partial class Behind
{
    private int total = 7;

    protected override void OnInitialized()
    {
        _ = JsonConvert.SerializeObject(new { total });
    }
}
PROGRAM
    )
fi
dotnet build "${ISSUE98}/components/Host/Host.csproj" -v quiet -p:NuGetAudit=false

echo "==> Corpus ready at ${CORPUS_ROOT}"

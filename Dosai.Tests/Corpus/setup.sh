#!/usr/bin/env bash
# Clones and builds the Dosai corpus test corpus at pinned commits.
# Run once before executing CorpusTests; see README.md for what each app covers.
set -euo pipefail

# DOSAI_CORPUS_DIR picks the corpus directory; unset, empty or blank means ~/sandbox/dosai-corpus.
# Surrounding white space is ignored, a leading ~ is expanded, and the result must be absolute.
# CorpusLocation applies the same rule for the tests.
CORPUS_ROOT="${DOSAI_CORPUS_DIR-}"
CORPUS_ROOT="${CORPUS_ROOT#"${CORPUS_ROOT%%[![:space:]]*}"}"
CORPUS_ROOT="${CORPUS_ROOT%"${CORPUS_ROOT##*[![:space:]]}"}"
case "${CORPUS_ROOT}" in
    "") CORPUS_ROOT="${HOME}/sandbox/dosai-corpus" ;;
    "~") CORPUS_ROOT="${HOME}" ;;
    "~/"*)
        rest="${CORPUS_ROOT#\~/}"
        while [ "${rest#/}" != "${rest}" ]; do rest="${rest#/}"; done
        CORPUS_ROOT="${HOME}/${rest}"
        ;;
esac
case "${CORPUS_ROOT}" in
    /*) ;;
    *)
        echo "DOSAI_CORPUS_DIR must be an absolute path, not '${DOSAI_CORPUS_DIR}'." >&2
        exit 1
        ;;
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

@code {
    public string Label()
        => JsonConvert.SerializeObject(new { total });
}
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

echo "==> issue127-tfm (issue #127 repro: net10.0 binding probes, plain and detected, plus a restore-only net48 legacy probe)"
# Builds the issue #127 fixture: probe projects that hold the same source and differ only
# in target framework and shared-framework declaration, so the methods command's framework
# binding can be compared across them. plain and detected build; legacy (net48) cannot be
# built on this macOS machine, so it is restored only and dosai reads its source unbuilt.
FIXTURE="${CORPUS_ROOT}/issue127-tfm"
rm -rf "${FIXTURE}"
mkdir -p "${FIXTURE}"

# The report's Probe.cs plus one TimeSpan call, so the binding check covers
# StringBuilder.Append, Console.WriteLine and TimeSpan.FromSeconds.
write_probe() {
    local project="$1"
    mkdir -p "${project}"
    cat > "${project}/Probe.cs" <<'PROGRAM'
using System;
using System.Text;

namespace BindingProbe;

public static class Probe
{
    public static string Run()
    {
        var builder = new StringBuilder();
        builder.Append("answer");
        Console.WriteLine(builder.ToString());
        _ = TimeSpan.FromSeconds(5);
        return builder.ToString();
    }
}
PROGRAM
}

echo "==> issue127-tfm/plain (net10.0, no FrameworkReference: nothing for TreeFrameworks to detect)"
write_probe "${FIXTURE}/plain"
cat > "${FIXTURE}/plain/plain.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
CSPROJ
dotnet build "${FIXTURE}/plain/plain.csproj" -v quiet

echo "==> issue127-tfm/detected (same net10.0 source plus FrameworkReference Microsoft.AspNetCore.App)"
write_probe "${FIXTURE}/detected"
cat > "${FIXTURE}/detected/detected.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
CSPROJ
dotnet build "${FIXTURE}/detected/detected.csproj" -v quiet

echo "==> issue127-tfm/legacy (net48; restore only, this machine cannot build net48)"
write_probe "${FIXTURE}/legacy"
cat > "${FIXTURE}/legacy/legacy.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
  </PropertyGroup>
</Project>
CSPROJ
dotnet restore "${FIXTURE}/legacy/legacy.csproj" -v quiet

echo "==> Fixture ready at ${FIXTURE}"

echo "==> issue134-weakhash (issue #134 repro: the report's Hashing.cs plus VB, lambda, Has-probe and boundary variants, source-only)"
# Creates the issue #134 fixture: a directory whose only file is the report's Hashing.cs,
# plus the report's python check and the report's variants in subdirectories.
# Source-only (no project), as in the report: dosai crypto analyzes it without a build.
ISSUE134="${CORPUS_ROOT}/issue134-weakhash"
rm -rf "${ISSUE134}"
mkdir -p "${ISSUE134}"

echo "==> issue134-weakhash (the report's Hashing.cs: arrow MD5 on line 5, block MD5 on line 9)"
cat > "${ISSUE134}/Hashing.cs" <<'PROGRAM'
using System.Security.Cryptography;

public static class Hashing
{
    public static byte[] Arrow(byte[] data) => CryptographicOperations.HashData(HashAlgorithmName.MD5, data);

    public static byte[] Block(byte[] data)
    {
        return CryptographicOperations.HashData(HashAlgorithmName.MD5, data);
    }
}
PROGRAM

# The report's one-liner, verbatim, as a file: run it from a directory holding crypto.json.
cat > "${ISSUE134}/check.py" <<'PYTHON'
import json; r = json.load(open('crypto.json', encoding='utf-8-sig')); [print(i['Location']['LineNumber'], i.get('Name') or i['RuleId']) for k in ('Assets', 'Findings') for i in r[k]]
PYTHON

echo "==> issue134-weakhash/vb (the report's VB Dim line)"
mkdir -p "${ISSUE134}/vb"
cat > "${ISSUE134}/vb/Hashing.vb" <<'PROGRAM'
Imports System.Security.Cryptography

Public Module HashingVb

    Public Function Hash(data As Byte()) As Byte()
        Dim hash = CryptographicOperations.HashData(HashAlgorithmName.MD5, Data)
        Return hash
    End Function

End Module
PROGRAM

echo "==> issue134-weakhash/lambda (the report's C# parenthesized lambda field initializer)"
mkdir -p "${ISSUE134}/lambda"
cat > "${ISSUE134}/lambda/ParenLambda.cs" <<'PROGRAM'
using System;
using System.Security.Cryptography;

public static class LambdaHost
{
    private static readonly Func<byte[], byte[]> ParenLambda = (d) => CryptographicOperations.HashData(HashAlgorithmName.MD5, d);
}
PROGRAM

echo "==> issue134-weakhash/hasprobe (the report's regex-fix probe: Has(RSA?) beside a real MD5 use)"
mkdir -p "${ISSUE134}/hasprobe"
cat > "${ISSUE134}/hasprobe/HasProbe.cs" <<'PROGRAM'
using System.Security.Cryptography;

public static class HasProbe
{
    public static bool Has(RSA? rsa) => rsa is not null;

    public static byte[] Block(byte[] data)
    {
        return CryptographicOperations.HashData(HashAlgorithmName.MD5, data);
    }
}
PROGRAM

echo "==> issue134-weakhash/boundary (extra probe: using-static MD5, and MD5 through a string literal)"
# The using-static arm names the call fully qualified and drops the namespace import: beside
# `using System.Security.Cryptography` the bare MD5 is CS0229-ambiguous between the type and
# the HashAlgorithmName.MD5 property, and a file that does not compile probes nothing.
mkdir -p "${ISSUE134}/boundary"
cat > "${ISSUE134}/boundary/Boundary.cs" <<'PROGRAM'
using static System.Security.Cryptography.HashAlgorithmName;

public static class Boundary
{
    public static byte[] ViaUsingStatic(byte[] data) => System.Security.Cryptography.CryptographicOperations.HashData(MD5, data);

    public static byte[] ViaString(byte[] data)
    {
        var algo = new System.Security.Cryptography.HashAlgorithmName("MD5");
        return System.Security.Cryptography.CryptographicOperations.HashData(algo, data);
    }
}
PROGRAM

echo "==> issue134-weakhash/hmac (the MAC half of the family: argument-named MAC beside a runtime algorithm)"
mkdir -p "${ISSUE134}/hmac"
cat > "${ISSUE134}/hmac/Hmac.cs" <<'PROGRAM'
using System;
using System.Security.Cryptography;

public static class Hmac
{
    public static byte[] Arrow(ReadOnlySpan<byte> key, byte[] data) => CryptographicOperations.HmacData(HashAlgorithmName.MD5, key, data);

    public static byte[] Block(ReadOnlySpan<byte> key, byte[] data)
    {
        return CryptographicOperations.HmacData(HashAlgorithmName.MD5, key, data);
    }

    public static byte[] Runtime(HashAlgorithmName name, ReadOnlySpan<byte> key, byte[] data) => CryptographicOperations.HmacData(name, key, data);
}
PROGRAM

echo "==> Fixture ready at ${ISSUE134}"

echo "==> issue130-initcalls (issue #130 repro: calls in field and auto-property initializers, one variant directory per report case and boundary probe)"
# Issue #130 repro fixture: `methods` records no calls made directly in field and
# auto-property initializers. Creates issue130-initcalls under the corpus root with
# one subdirectory per variant of the report plus the boundary probes.
FIXTURE="${CORPUS_ROOT}/issue130-initcalls"
rm -rf "${FIXTURE}"
mkdir -p "${FIXTURE}"

# The report's Repro.csproj, shared by every variant.
write_project() {
    mkdir -p "$1"
    cat > "$1/Repro.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
</Project>
CSPROJ
}

# The report's check.py, verbatim, plus a variant that also prints EvidenceKind.
cat > "${FIXTURE}/check.py" <<'CHECK'
import json, sys

report = json.load(open(sys.argv[1], encoding="utf-8"))
for call in report["MethodCalls"]:
    if call["FileName"] == "Settings.cs":
        print(call["LineNumber"], call["CallerMethod"], "->", call["CalledMethod"])
for entry in report["PackageReachability"]:
    if entry["Purl"].startswith("pkg:nuget/Newtonsoft.Json"):
        print(entry["Purl"], entry["ReachabilityKind"], entry["Confidence"])
CHECK
cat > "${FIXTURE}/checkex.py" <<'CHECK'
import json, sys

report = json.load(open(sys.argv[1], encoding="utf-8"))
for call in report["MethodCalls"]:
    if call["FileName"] == "Settings.cs":
        print(call["LineNumber"], call["CallerMethod"], "->", call["CalledMethod"],
              "CallType=" + str(call["CallType"]), "EvidenceKind=" + str(call["EvidenceKind"]))
for entry in report["PackageReachability"]:
    if entry["Purl"].startswith("pkg:nuget/Newtonsoft.Json"):
        print(entry["Purl"], entry["ReachabilityKind"], entry["Confidence"])
CHECK

# base: the report's exact Settings.cs, restored. Lines 7-9 are the initializer calls
# the report says get no row; line 10 is the lambda that does.
write_project "${FIXTURE}/base"
cat > "${FIXTURE}/base/Settings.cs" <<'CS'
using Newtonsoft.Json;

namespace Repro;

public class Settings
{
    private static readonly string StaticField = JsonConvert.SerializeObject(1);
    private readonly JsonSerializerSettings instanceField = new JsonSerializerSettings();
    public string AutoProperty { get; } = JsonConvert.SerializeObject(2);
    private static readonly Func<string> LambdaField = () => JsonConvert.SerializeObject(3);
}
CS
dotnet restore "${FIXTURE}/base/Repro.csproj" -v quiet

# norestore: same files, never restored (the SourceUnresolved variant of the report).
write_project "${FIXTURE}/norestore"
cat > "${FIXTURE}/norestore/Settings.cs" <<'CS'
using Newtonsoft.Json;

namespace Repro;

public class Settings
{
    private static readonly string StaticField = JsonConvert.SerializeObject(1);
    private readonly JsonSerializerSettings instanceField = new JsonSerializerSettings();
    public string AutoProperty { get; } = JsonConvert.SerializeObject(2);
    private static readonly Func<string> LambdaField = () => JsonConvert.SerializeObject(3);
}
CS

# nolambda: the report's Settings.cs with line 10 (LambdaField) removed, restored.
write_project "${FIXTURE}/nolambda"
cat > "${FIXTURE}/nolambda/Settings.cs" <<'CS'
using Newtonsoft.Json;

namespace Repro;

public class Settings
{
    private static readonly string StaticField = JsonConvert.SerializeObject(1);
    private readonly JsonSerializerSettings instanceField = new JsonSerializerSettings();
    public string AutoProperty { get; } = JsonConvert.SerializeObject(2);
}
CS
dotnet restore "${FIXTURE}/nolambda/Repro.csproj" -v quiet

# qualified: no using directive, fully qualified names, restored.
write_project "${FIXTURE}/qualified"
cat > "${FIXTURE}/qualified/Settings.cs" <<'CS'
namespace Repro;

public class Settings
{
    private static readonly string StaticField = Newtonsoft.Json.JsonConvert.SerializeObject(1);
    private readonly Newtonsoft.Json.JsonSerializerSettings instanceField = new Newtonsoft.Json.JsonSerializerSettings();
    public string AutoProperty { get; } = Newtonsoft.Json.JsonConvert.SerializeObject(2);
    private static readonly Func<string> LambdaField = () => Newtonsoft.Json.JsonConvert.SerializeObject(3);
}
CS
dotnet restore "${FIXTURE}/qualified/Repro.csproj" -v quiet

# staticctor: an explicit static constructor written by the user.
write_project "${FIXTURE}/staticctor"
cat > "${FIXTURE}/staticctor/Settings.cs" <<'CS'
using Newtonsoft.Json;

namespace Repro;

public class Settings
{
    private static readonly string Field = "seed";

    static Settings()
    {
        Field = JsonConvert.SerializeObject(Field);
    }
}
CS
dotnet restore "${FIXTURE}/staticctor/Repro.csproj" -v quiet

# instancector: an explicit instance constructor written by the user.
write_project "${FIXTURE}/instancector"
cat > "${FIXTURE}/instancector/Settings.cs" <<'CS'
using Newtonsoft.Json;

namespace Repro;

public class Settings
{
    public Settings()
    {
        var value = JsonConvert.SerializeObject(1);
        _ = value;
    }
}
CS
dotnet restore "${FIXTURE}/instancector/Repro.csproj" -v quiet

# structinit: field initializers on a struct (static and instance).
write_project "${FIXTURE}/structinit"
cat > "${FIXTURE}/structinit/Settings.cs" <<'CS'
using Newtonsoft.Json;

namespace Repro;

public struct Settings
{
    private static readonly string StaticField = JsonConvert.SerializeObject(1);
    public string AutoProperty { get; } = JsonConvert.SerializeObject(2);
}
CS
dotnet restore "${FIXTURE}/structinit/Repro.csproj" -v quiet

# attrarg: calls in attribute arguments on fields (positional and named). Not valid
# constant expressions for the compiler, but dosai analyzes source, so the question
# is what the call walker does with them.
write_project "${FIXTURE}/attrarg"
cat > "${FIXTURE}/attrarg/Settings.cs" <<'CS'
using Newtonsoft.Json;

namespace Repro;

public sealed class MarkAttribute : Attribute
{
    public MarkAttribute(string value) { }
    public string Named { get; set; } = "";
}

public class Settings
{
    [Mark(JsonConvert.SerializeObject(1))]
    private static readonly string PositionalField = "a";

    [Mark(Named = JsonConvert.SerializeObject(2))]
    private static readonly string NamedField = "b";
}
CS
dotnet restore "${FIXTURE}/attrarg/Repro.csproj" -v quiet

# built: the base repro, built, so the IL call graph pass can also see the tree.
write_project "${FIXTURE}/built"
cat > "${FIXTURE}/built/Settings.cs" <<'CS'
using Newtonsoft.Json;

namespace Repro;

public class Settings
{
    private static readonly string StaticField = JsonConvert.SerializeObject(1);
    private readonly JsonSerializerSettings instanceField = new JsonSerializerSettings();
    public string AutoProperty { get; } = JsonConvert.SerializeObject(2);
    private static readonly Func<string> LambdaField = () => JsonConvert.SerializeObject(3);
}
CS
dotnet build "${FIXTURE}/built/Repro.csproj" -v quiet -p:NuGetAudit=false

echo "==> Fixture ready at ${FIXTURE}"

echo "==> issue128-libpacks (issue #128 repro: LibA/LibB and LibP/LibQ packed as local 1.0.0 packages, main, control and probe trees)"
# Issue #128 reproduction: --dependency-flows reachable analyzes unreachable package
# method bodies because the reach walk keys members by the namespace-joined type name
# (Dosai/DataFlowAssembly.cs MethodKey/FieldKey through GetFullTypeName), and a nested
# type has no namespace: every assembly's <>c is one key.
#
# Three trees under <corpus>/issue128-libpacks, each with its own local feed:
#   main    the report's tree: LibA and LibB packed as packages, App references both and
#           calls only LibA; LibB.NeverCalled holds a lambda. Expect "5 of 13".
#   control the report's control: LibB.NeverCalled reads inputs[0] directly, no lambda.
#           Expect "6 of 10".
#   probe   two packages whose private nested class is literally named Cache: LibP is
#           called, LibQ never is. Tests whether the collision needs a compiler-generated
#           name or whether any namespace-less nested type name collides.
#
# Every variant keeps package version 1.0.0, so the shared ids (LibA, LibB) are cleared
# from the NuGet cache before each pack; a variant must not restore another variant's
# package from the global cache.
# Self-check only: after each variant is built, print the "not analyzed" diagnostic the
# issue reproduces, so a freshly generated fixture shows it is healthy. DOSAI overrides;
# the default resolves the CLI from this checkout (setup.sh lives at Dosai.Tests/Corpus/).
DOSAI_SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOSAI="${DOSAI:-dotnet run --project "${DOSAI_SCRIPT_DIR}/../../Dosai/Dosai.csproj" -c Release --}"

FIXTURE="${CORPUS_ROOT}/issue128-libpacks"
rm -rf "${FIXTURE}"
mkdir -p "${FIXTURE}"

write_nuget_config() {
    cat > "$1/nuget.config" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="./feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML
}

write_lib_project() {
    local dir="$1" id="$2"
    mkdir -p "${dir}"
    cat > "${dir}/${id}.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <PackageId>${id}</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>
</Project>
XML
}

clear_nuget_cache() {
    rm -rf "${HOME}/.nuget/packages/$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')"
}

analyze_variant() {
    local variant="$1"
    ${DOSAI} dataflows --dependency-flows reachable \
        --path "${variant}/App/bin/Release/net10.0" --o "${variant}/out.json"
    python3 -c "
import json
report = json.load(open('${variant}/out.json', encoding='utf-8-sig'))
for note in report.get('Diagnostics') or []:
    if 'not analyzed' in note:
        print(note)
"
}

echo "==> issue128-libpacks/main: the report's tree (LibB.NeverCalled holds a lambda)"
MAIN="${FIXTURE}/main"
mkdir -p "${MAIN}/App"
write_lib_project "${MAIN}/LibA" LibA
write_lib_project "${MAIN}/LibB" LibB

cat > "${MAIN}/LibA/A.cs" <<'CS'
using System.Linq;

namespace LibA;

public static class A
{
    public static int Run(int[] values)
    {
        return values.Where(v => v > 0).Sum();
    }
}
CS

cat > "${MAIN}/LibB/B.cs" <<'CS'
using System.Diagnostics;
using System.Linq;

namespace LibB;

public static class B
{
    public static void NeverCalled(string[] inputs)
    {
        var chosen = inputs.Where(v => v.Length > 0).ToArray();
        Process.Start("cmd.exe", chosen[0]);
    }

    public static int Unused1(int a) => a + 1;
    public static int Unused2(int a) => a + 2;
    public static int Unused3(int a) => a + 3;
    public static int Unused4(int a) => a + 4;
    public static int Unused5(int a) => a + 5;
}
CS

write_nuget_config "${MAIN}"

cat > "${MAIN}/App/App.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="LibA" Version="1.0.0" />
    <PackageReference Include="LibB" Version="1.0.0" />
  </ItemGroup>
</Project>
XML

cat > "${MAIN}/App/Program.cs" <<'CS'
using System;
using System.Linq;

namespace App;

public static class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine(LibA.A.Run(args.Select(int.Parse).ToArray()));
    }
}
CS

clear_nuget_cache LibA
clear_nuget_cache LibB
dotnet pack "${MAIN}/LibA/LibA.csproj" -c Release -o "${MAIN}/feed" -v quiet
dotnet pack "${MAIN}/LibB/LibB.csproj" -c Release -o "${MAIN}/feed" -v quiet
dotnet build "${MAIN}/App/App.csproj" -c Release -v quiet
analyze_variant "${MAIN}"

echo "==> issue128-libpacks/control: LibB.NeverCalled reads inputs[0] directly (no lambda)"
CONTROL="${FIXTURE}/control"
mkdir -p "${CONTROL}/App"
write_lib_project "${CONTROL}/LibA" LibA
write_lib_project "${CONTROL}/LibB" LibB

cp "${MAIN}/LibA/A.cs" "${CONTROL}/LibA/A.cs"

cat > "${CONTROL}/LibB/B.cs" <<'CS'
using System.Diagnostics;
using System.Linq;

namespace LibB;

public static class B
{
    public static void NeverCalled(string[] inputs)
    {
        Process.Start("cmd.exe", inputs[0]);
    }

    public static int Unused1(int a) => a + 1;
    public static int Unused2(int a) => a + 2;
    public static int Unused3(int a) => a + 3;
    public static int Unused4(int a) => a + 4;
    public static int Unused5(int a) => a + 5;
}
CS

write_nuget_config "${CONTROL}"
cp "${MAIN}/App/App.csproj" "${CONTROL}/App/App.csproj"
cp "${MAIN}/App/Program.cs" "${CONTROL}/App/Program.cs"

clear_nuget_cache LibA
clear_nuget_cache LibB
dotnet pack "${CONTROL}/LibA/LibA.csproj" -c Release -o "${CONTROL}/feed" -v quiet
dotnet pack "${CONTROL}/LibB/LibB.csproj" -c Release -o "${CONTROL}/feed" -v quiet
dotnet build "${CONTROL}/App/App.csproj" -c Release -v quiet
analyze_variant "${CONTROL}"

echo "==> issue128-libpacks/probe: user-named nested Cache classes in LibP and LibQ"
PROBE="${FIXTURE}/probe"
mkdir -p "${PROBE}/App"
write_lib_project "${PROBE}/LibP" LibP
write_lib_project "${PROBE}/LibQ" LibQ

cat > "${PROBE}/LibP/P.cs" <<'CS'
using System.Linq;

namespace LibP;

public static class P
{
    public static int Run(int[] values)
    {
        return Cache.Local(values[0]) + values.Sum();
    }

    private static class Cache
    {
        public static int Local(int seed) => seed + 1;
    }
}
CS

cat > "${PROBE}/LibQ/Q.cs" <<'CS'
using System.Diagnostics;

namespace LibQ;

public static class Q
{
    public static void NeverCalled(string[] inputs)
    {
        Cache.Local(inputs[0]);
    }

    public static int Unused1(int a) => a + 1;
    public static int Unused2(int a) => a + 2;

    private static class Cache
    {
        public static void Local(string seed)
        {
            Process.Start("cmd.exe", seed);
        }
    }
}
CS

write_nuget_config "${PROBE}"

cat > "${PROBE}/App/App.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="LibP" Version="1.0.0" />
    <PackageReference Include="LibQ" Version="1.0.0" />
  </ItemGroup>
</Project>
XML

cat > "${PROBE}/App/Program.cs" <<'CS'
using System;
using System.Linq;

namespace App;

public static class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine(LibP.P.Run(args.Select(int.Parse).ToArray()));
    }
}
CS

clear_nuget_cache LibP
clear_nuget_cache LibQ
dotnet pack "${PROBE}/LibP/LibP.csproj" -c Release -o "${PROBE}/feed" -v quiet
dotnet pack "${PROBE}/LibQ/LibQ.csproj" -c Release -o "${PROBE}/feed" -v quiet
dotnet build "${PROBE}/App/App.csproj" -c Release -v quiet
analyze_variant "${PROBE}"

echo "==> issue120-serilog (issue #120 repro: the report's Serilog console app plus a formatter boundary probe)"
# Builds the issue #120 corpus fixture: the report's net8.0 console app with Serilog 4.4.0
# and Serilog.Sinks.Console 6.1.1, plus a probe subdirectory whose app news a Serilog
# formatter directly and calls it through ITextFormatter (a flow reachable mode should keep).
FIXTURE="${CORPUS_ROOT}/issue120-serilog"
rm -rf "${FIXTURE}"
mkdir -p "${FIXTURE}/App" "${FIXTURE}/probe"

cat > "${FIXTURE}/nuget.config" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

cat > "${FIXTURE}/App/App.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Serilog" Version="4.4.0" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.1.1" />
  </ItemGroup>

</Project>
CSPROJ

cat > "${FIXTURE}/App/Program.cs" <<'CS'
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

try
{
    Lookup(args[0]);
}
catch (Exception ex)
{
    Log.Error(ex, "lookup failed");
}

Log.CloseAndFlush();

static void Lookup(string email)
{
    throw new InvalidOperationException($"There is no user found with Email: {email}");
}
CS

dotnet build "${FIXTURE}/App/App.csproj" -c Release -v quiet -p:NuGetAudit=false

echo "==> probe (issue #120 boundary probe: the app news the formatter itself and calls it through ITextFormatter)"
cat > "${FIXTURE}/probe/Probe.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Serilog" Version="4.4.0" />
  </ItemGroup>

</Project>
CSPROJ

cat > "${FIXTURE}/probe/Program.cs" <<'CS'
using Serilog.Events;
using Serilog.Formatting.Display;

var formatter = (Serilog.Formatting.ITextFormatter)new MessageTemplateTextFormatter("{Exception}", null);

try
{
    Lookup(args[0]);
}
catch (Exception ex)
{
    var evt = new LogEvent(DateTimeOffset.Now, LogEventLevel.Error, ex, MessageTemplate.Empty, Enumerable.Empty<LogEventProperty>());
    formatter.Format(evt, Console.Out);
}

static void Lookup(string email)
{
    throw new InvalidOperationException($"There is no user found with Email: {email}");
}
CS

dotnet build "${FIXTURE}/probe/Probe.csproj" -c Release -v quiet -p:NuGetAudit=false

echo "==> issue120 fixture ready at ${FIXTURE}"

# Writes the issue #131 repro: a net10.0 tree whose only flows live in field and
# property initializers, expression-bodied members and a primary constructor base
# argument. The fixture must stay UNBUILT (restore only): the point of the issue is
# that the source pass finds none of these flows until bin/ exists.
echo "==> issue131-initflows (issue #131 repro: initializers and expression bodies, restore only)"
ISSUE131="${CORPUS_ROOT}/issue131-initflows"
rm -rf "${ISSUE131}"
mkdir -p "${ISSUE131}"
cat > "${ISSUE131}/Repro.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
CSPROJ
cat > "${ISSUE131}/Flows.cs" <<'PROGRAM'
using System.Diagnostics;

namespace Repro;

public class Flows
{
    private static readonly string FromField = Console.ReadLine()!;
    public static Process UseField() => Process.Start(FromField);
    public Process AutoProperty { get; } = Process.Start(Console.ReadLine()!);
    public static string Input => Console.ReadLine()!;
    public static Process UseInput() => Process.Start(Input);
    public Process this[int i] => Process.Start(Console.ReadLine()!);
    public static Process Direct() => Process.Start(Console.ReadLine()!);
}

public class Base(Process process)
{
    public Process Started => process;
}

public class Derived() : Base(Process.Start(Console.ReadLine()!));
PROGRAM
cat > "${ISSUE131}/check.py" <<'PYTHON'
import json, sys

report = json.load(open(sys.argv[1], encoding="utf-8"))
nodes = {node["Id"]: node for node in report["Nodes"]}
for flow in sorted({
    (nodes[s["SourceId"]]["LineNumber"], nodes[s["SinkId"]]["LineNumber"], nodes[s["SourceId"]]["Properties"].get("analysis", "source"))
    for s in report["Slices"]
}):
    print("line", flow[0], "-> line", flow[1], flow[2])
PYTHON

# Restore and nothing else: the fixture must stay unbuilt for the reproduction.
dotnet restore "${ISSUE131}/Repro.csproj" -v quiet

echo "==> issue131 fixture ready (unbuilt) at ${ISSUE131}"

# The same tree built, where the IL pass does find the initializer flows: the
# contrast between the two is the reproduction.
ISSUE131BUILT="${CORPUS_ROOT}/issue131-initflows-built"
rm -rf "${ISSUE131BUILT}"
mkdir -p "${ISSUE131BUILT}"
cp "${ISSUE131}/Repro.csproj" "${ISSUE131}/Flows.cs" "${ISSUE131}/check.py" "${ISSUE131BUILT}/"
dotnet build "${ISSUE131BUILT}/Repro.csproj" -v quiet

echo "==> issue131 built fixture ready at ${ISSUE131BUILT}"

# A second probe: a static field initializer feeding a block-bodied method, the
# shape whose cross-member taint only the IL pass tracks today.
ISSUE131PROBE="${CORPUS_ROOT}/issue131-initflows-probe"
rm -rf "${ISSUE131PROBE}"
mkdir -p "${ISSUE131PROBE}"
cp "${ISSUE131}/Repro.csproj" "${ISSUE131PROBE}/"
cat > "${ISSUE131PROBE}/Probe.cs" <<'PROGRAM'
using System.Diagnostics;

namespace Probe;

public class P
{
    private static readonly string FromField = Console.ReadLine()!;

    public static Process UseFieldBlock()
    {
        return Process.Start(FromField);
    }
}
PROGRAM
dotnet build "${ISSUE131PROBE}/Repro.csproj" -v quiet

echo "==> issue131 probe fixture ready (built) at ${ISSUE131PROBE}"

echo "==> issue129-edgeids (issue #129 repro: two calls to the same package method from one method)"
# Issue #129 repro: methods lists PackageReachability EdgeIds that are not in
# CallGraph.Edges when a method calls the same package method more than once.
# Run once before analyzing; see https://github.com/owasp-dep-scan/dosai/issues/129
ISSUE129="${CORPUS_ROOT}/issue129-edgeids"
rm -rf "${ISSUE129}"
mkdir -p "${ISSUE129}"

cat > "${ISSUE129}/Repro.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
</Project>
CSPROJ
cat > "${ISSUE129}/Program.cs" <<'PROGRAM'
using Newtonsoft.Json;

var a = JsonConvert.SerializeObject(new { x = 1 });
var b = JsonConvert.SerializeObject(new { y = 2 });
Console.WriteLine(a.Length + b.Length);
PROGRAM

# The report's check, with utf-8-sig because a build's output can carry a BOM.
cat > "${ISSUE129}/check.py" <<'CHECK'
import json, sys

report = json.load(open(sys.argv[1], encoding="utf-8-sig"))
edges = {edge["Id"] for edge in report["CallGraph"]["Edges"]}
for entry in report["PackageReachability"]:
    missing = [edge_id for edge_id in entry["EdgeIds"] if edge_id not in edges]
    if missing:
        print(entry["Purl"], "EdgeIds", entry["EdgeIds"], "missing from CallGraph.Edges", missing)
CHECK

# Source-only analysis is enough for the issue; restore resolves the package closure.
dotnet restore "${ISSUE129}/Repro.csproj" -v quiet

echo "==> Issue #129 fixture ready at ${ISSUE129}"

echo "==> Corpus ready at ${CORPUS_ROOT}"

// A file-based ASP.NET Core app fixture for the `methods --sections` tests: one source file
// whose report fills as many top-level sections as a source-only tree can - minimal API
// endpoints, DI registrations, properties, fields, events, constructors, mutual recursion (a
// recursion cluster), an unreachable private helper (dead code), and `#:package` references
// that back dependency-backed package reachability facts.
//
// Source-mode fixture only, like FileBasedApp.cs: compiling it into Dosai.TestData.CSharp
// would change that assembly's member set and with it the method tables the assembly-mode
// tests expect.
#:sdk Microsoft.NET.Sdk.Web
#:package Microsoft.Extensions.Logging@9.0.0
#:package Newtonsoft.Json@13.0.3

using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(OrderBook.Current);
var app = builder.Build();

app.MapGet("/orders/{id}", (int id) => Results.Json(OrderBook.Current.Describe(id)));
app.MapPost("/orders", (OrderRequest request) => Results.Text(OrderBook.Current.Add(request.Sku, request.Quantity)));
app.MapGet("/health", () => Results.Text("ok"));
app.Run();

public sealed class OrderRequest
{
    public string Sku { get; set; } = string.Empty;

    public int Quantity { get; set; }
}

public sealed class OrderBook
{
    private readonly List<string> orders = [];
    private int nextId = 1;

    public static OrderBook Current { get; } = new();

    public event EventHandler? Changed;

    public OrderBook()
    {
        orders.Add("seed:0");
    }

    public string Describe(int id) => id < orders.Count ? JsonSerializer.Serialize(new { id, order = orders[id] }) : "none";

    public string Add(string sku, int quantity)
    {
        var id = nextId++;
        var order = $"{id}:{sku}x{quantity}";
        orders.Add(order);
        Changed?.Invoke(this, EventArgs.Empty);
        return order;
    }

    internal int Walk(int steps) => steps <= 0 ? 0 : Step(steps);

    private int Step(int steps) => steps <= 0 ? 0 : 1 + Walk(steps - 1);

    private string NeverMentionedAgain()
    {
        // No entry point reaches this and no reflection/DI evidence keeps it alive: dead code.
        return JsonSerializer.Serialize(orders);
    }
}

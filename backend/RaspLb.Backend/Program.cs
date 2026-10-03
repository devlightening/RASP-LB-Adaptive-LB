using System.Globalization;
using RaspLb.Backend.Admission;
using RaspLb.Backend.Brownout;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddHealthChecks();

var maxConcurrency =
    GetInt("MAX_CONCURRENCY", 100);

// Load shedding (Aşama F). Kuyruk varsayılan olarak kapasitenin 4 katıyla
// ve 250 ms beklemeyle sınırlı - SLO deadline'ı (500 ms) dolmadan önce
// "bu istek zaten geç kalacak" kararını verip erken 503 dönebilmek için.
var sheddingEnabled =
    GetBool("SHEDDING_ENABLED", true);

var maxQueueLength =
    GetInt("MAX_QUEUE_LENGTH", maxConcurrency * 4);

var queueTimeoutMs =
    GetInt("QUEUE_TIMEOUT_MS", 250);

var baseLatencyMs =
    GetInt("BASE_LATENCY_MS", 50);

builder.Services.AddSingleton(
    new AdmissionGate(
        maxConcurrency,
        sheddingEnabled,
        maxQueueLength,
        queueTimeoutMs,
        initialServiceMs: baseLatencyMs));

builder.Services.AddSingleton(
    new BrownoutState(
        GetBool("BROWNOUT_ENABLED", true),
        GetDouble("BROWNOUT_QUEUE_WAIT_MS", 50),
        GetInt("BROWNOUT_MIN_DWELL_MS", 2000)));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapControllers();

app.MapHealthChecks("/health");

app.MapGet("/debug/brownout", (
    AdmissionGate admissionGate,
    BrownoutState brownoutState) =>
{
    return Results.Ok(
        brownoutState.GetSnapshot(
            admissionGate.EstimatedQueueWaitMs()));
});

// Tek çağrıda backend'in anlık durumu - gateway dashboard için bunu okur.
app.MapGet("/debug/state", (
    AdmissionGate admissionGate,
    BrownoutState brownoutState) =>
{
    return Results.Ok(new
    {
        Instance =
            Environment.GetEnvironmentVariable("INSTANCE_NAME")
            ?? Environment.MachineName,
        Admission = admissionGate.GetSnapshot(),
        Brownout = brownoutState.GetSnapshot(
            admissionGate.EstimatedQueueWaitMs())
    });
});

app.Run();

static int GetInt(
    string name,
    int defaultValue)
{
    return int.TryParse(
        Environment.GetEnvironmentVariable(name),
        out var value)
        ? value
        : defaultValue;
}

static double GetDouble(
    string name,
    double defaultValue)
{
    return double.TryParse(
        Environment.GetEnvironmentVariable(name),
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
        out var value)
        ? value
        : defaultValue;
}

static bool GetBool(
    string name,
    bool defaultValue)
{
    return bool.TryParse(
        Environment.GetEnvironmentVariable(name),
        out var value)
        ? value
        : defaultValue;
}

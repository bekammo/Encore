using Encore.Modules.Payments;
using Encore.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.AddEncoreTelemetry("encore-payments");

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

builder.Services.AddPaymentsModule(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapEncoreHealthChecks();

app.MapPaymentsModule();
app.MapPaymentsServiceApi(builder.Configuration);

app.Run();

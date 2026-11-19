var builder = WebApplication.CreateBuilder(args);

// OpenAPI describes the API's endpoints as a JSON document. Phase 3 adds a browsable UI on top.
builder.Services.AddOpenApi();

// Health checks answer one question: "is this service up?" Phase 2 adds a database check here.
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Returns status 200 and text "Healthy" if the service is up, or 503 and "Unhealthy" if not.
app.MapHealthChecks("/health");

app.Run();
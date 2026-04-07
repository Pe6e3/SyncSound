var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("ClientPolicy", policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors("ClientPolicy");

app.MapGet("/api/time", () =>
{
    var unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    return Results.Ok(new { unixSeconds });
});

app.Run();

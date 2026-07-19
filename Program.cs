namespace AITextGame;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers();
        builder.Services.AddOpenApi();
        builder.Services.AddHttpClient();
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
            policy.WithOrigins("https://zerocr54-code.github.io")
                  .AllowAnyMethod()
                  .AllowAnyHeader()));

        var app = builder.Build();
        app.UseCors();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        if (app.Environment.IsDevelopment()) app.MapOpenApi();
        app.MapControllers();
        app.Run();
    }
}

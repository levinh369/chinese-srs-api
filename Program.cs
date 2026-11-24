using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// B?t CORS ?? frontend (nh? Vercel) g?i ???c API cross-origin
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowSrsClient", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors("AllowSrsClient");
app.UseAuthorization();
app.MapControllers();

// Ki?m tra k?t n?i PostgreSQL (Neon) khi kh?i ??ng
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
              ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");

if (!string.IsNullOrWhiteSpace(connStr))
{
    try
    {
        using var conn = new NpgsqlConnection(connStr);
        conn.Open();
        Console.WriteLine("?ã k?t n?i PostgreSQL (Neon) thành công cho SRS Backend (.NET)!");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"C?nh báo: Ch?a k?t n?i ???c PostgreSQL lúc kh?i ??ng: {ex.Message}");
    }
}
else
{
    Console.WriteLine("C?nh báo: Ch?a c?u hình ConnectionStrings__DefaultConnection.");
}

app.Run();
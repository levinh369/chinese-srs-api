using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ChineseSrsApi.Controllers;

[ApiController]
[Route("api/srs")]
public class SrsApiController : ControllerBase
{
    private readonly string _connectionString;
    private static bool _schemaInitialized;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SrsApiController(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
                            ?? throw new InvalidOperationException("Thiếu ConnectionStrings__DefaultConnection.");
    }

    [HttpGet]
    public async Task<IActionResult> HealthCheck(CancellationToken ct)
    {
        try
        {
            await EnsureSchemaAsync(ct);
            return Ok(new { ok = true, status = "online", message = "SRS API (.NET 8 + Neon) sẵn sàng." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { ok = false, message = $"Lỗi kết nối DB: {ex.Message}" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> HandlePost([FromBody] JsonElement body, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);

        string action = body.TryGetProperty("action", out var actProp) && actProp.ValueKind == JsonValueKind.String
            ? actProp.GetString()!.Trim().ToLowerInvariant()
            : "";

        try
        {
            return action switch
            {
                "" or "ping" or "health" => Ok(new { ok = true, status = "online" }),
                "register" => await RegisterAsync(body, ct),
                "login" => await LoginAsync(body, ct),
                "pull" or "sync_pull" or "get_all" => await PullDataAsync(body, ct),
                "push" or "sync_push" or "sync" => await PushDataAsync(body, ct),
                "get_account" => await GetAccountAsync(body, ct),
                "save_account" => await SaveAccountAsync(body, ct),
                "get_state" => await GetStateAsync(body, ct),
                "save_state" => await SaveStateAsync(body, ct),
                _ => BadRequest(new { ok = false, message = $"Action '{action}' không được hỗ trợ." })
            };
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { ok = false, message = $"Lỗi xử lý API: {ex.Message}" });
        }
    }

    private async Task<IActionResult> GetAccountAsync(JsonElement body, CancellationToken ct)
    {
        string key = ExtractKey(body);
        if (string.IsNullOrWhiteSpace(key))
            return Ok(new { ok = true, data = (object?)null });

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT data_json::text FROM cloud_kv WHERE ns = 'account' AND item_key = @k LIMIT 1", conn);
        cmd.Parameters.AddWithValue("k", key);
        var raw = await cmd.ExecuteScalarAsync(ct) as string;

        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
            return Ok(new { ok = true, data = (object?)null });

        using var doc = JsonDocument.Parse(raw);
        return Ok(new { ok = true, data = doc.RootElement.Clone() });
    }

    private async Task<IActionResult> SaveAccountAsync(JsonElement body, CancellationToken ct)
    {
        string key = ExtractKey(body);
        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(new { ok = false, message = "Thiếu key/userId cho save_account." });

        string payloadJson = ExtractPayloadJson(body);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO cloud_kv (ns, item_key, data_json, updated_at)
            VALUES ('account', @k, @j::jsonb, @u)
            ON CONFLICT (ns, item_key) DO UPDATE SET
                data_json = EXCLUDED.data_json,
                updated_at = EXCLUDED.updated_at", conn);
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("j", payloadJson);
        cmd.Parameters.AddWithValue("u", now);
        await cmd.ExecuteNonQueryAsync(ct);

        return Ok(new { ok = true });
    }

    private async Task<IActionResult> GetStateAsync(JsonElement body, CancellationToken ct)
    {
        string key = ExtractKey(body);
        if (string.IsNullOrWhiteSpace(key))
            return Ok(new { ok = true, data = (object?)null });

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT data_json::text FROM cloud_kv WHERE ns = 'state' AND item_key = @k LIMIT 1", conn);
        cmd.Parameters.AddWithValue("k", key);
        var raw = await cmd.ExecuteScalarAsync(ct) as string;

        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
            return Ok(new { ok = true, data = (object?)null });

        using var doc = JsonDocument.Parse(raw);
        return Ok(new { ok = true, data = doc.RootElement.Clone() });
    }

    private async Task<IActionResult> SaveStateAsync(JsonElement body, CancellationToken ct)
    {
        string key = ExtractKey(body);
        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(new { ok = false, message = "Thiếu key/userId cho save_state." });

        string payloadJson = ExtractPayloadJson(body);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO cloud_kv (ns, item_key, data_json, updated_at)
            VALUES ('state', @k, @j::jsonb, @u)
            ON CONFLICT (ns, item_key) DO UPDATE SET
                data_json = EXCLUDED.data_json,
                updated_at = EXCLUDED.updated_at", conn);
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("j", payloadJson);
        cmd.Parameters.AddWithValue("u", now);
        await cmd.ExecuteNonQueryAsync(ct);

        return Ok(new { ok = true });
    }

    private async Task<IActionResult> RegisterAsync(JsonElement body, CancellationToken ct)
    {
        string username = GetString(body, "username").Trim();
        string password = GetString(body, "password");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return Ok(new { ok = false, message = "Vui lòng nhập đầy đủ tên đăng nhập và mật khẩu." });

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var checkCmd = new NpgsqlCommand("SELECT id FROM users WHERE LOWER(username) = LOWER(@u) LIMIT 1", conn))
        {
            checkCmd.Parameters.AddWithValue("u", username);
            var exists = await checkCmd.ExecuteScalarAsync(ct);
            if (exists != null)
                return Ok(new { ok = false, message = "Tên đăng nhập đã tồn tại." });
        }

        string userId = Guid.NewGuid().ToString("N");
        string passwordHash = HashPassword(password);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using (var insertCmd = new NpgsqlCommand(@"
            INSERT INTO users (id, username, password_hash, created_at)
            VALUES (@id, @username, @password_hash, @created_at)", conn))
        {
            insertCmd.Parameters.AddWithValue("id", userId);
            insertCmd.Parameters.AddWithValue("username", username);
            insertCmd.Parameters.AddWithValue("password_hash", passwordHash);
            insertCmd.Parameters.AddWithValue("created_at", now);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var settingsCmd = new NpgsqlCommand(@"
            INSERT INTO user_settings (user_id, settings_json, updated_at)
            VALUES (@user_id, '{}', @updated_at)
            ON CONFLICT (user_id) DO NOTHING", conn))
        {
            settingsCmd.Parameters.AddWithValue("user_id", userId);
            settingsCmd.Parameters.AddWithValue("updated_at", now);
            await settingsCmd.ExecuteNonQueryAsync(ct);
        }

        return Ok(new
        {
            ok = true,
            user = new { id = userId, username },
            words = Array.Empty<object>(),
            settings = new { }
        });
    }

    private async Task<IActionResult> LoginAsync(JsonElement body, CancellationToken ct)
    {
        string username = GetString(body, "username").Trim();
        string password = GetString(body, "password");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return Ok(new { ok = false, message = "Vui lòng nhập tên đăng nhập và mật khẩu." });

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        string? userId = null;
        string? dbUsername = null;
        string? passwordHash = null;

        await using (var cmd = new NpgsqlCommand(@"
            SELECT id, username, password_hash
            FROM users
            WHERE LOWER(username) = LOWER(@u)
            LIMIT 1", conn))
        {
            cmd.Parameters.AddWithValue("u", username);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                userId = reader.GetString(0);
                dbUsername = reader.GetString(1);
                passwordHash = reader.GetString(2);
            }
        }

        if (userId == null || passwordHash == null || !VerifyPassword(password, passwordHash))
            return Ok(new { ok = false, message = "Sai tên đăng nhập hoặc mật khẩu." });

        var payload = await LoadUserPayloadAsync(conn, userId, ct);
        return Ok(new
        {
            ok = true,
            user = new { id = userId, username = dbUsername },
            words = payload.Words,
            settings = payload.Settings
        });
    }

    private async Task<IActionResult> PullDataAsync(JsonElement body, CancellationToken ct)
    {
        string userId = GetString(body, "userId");
        if (string.IsNullOrWhiteSpace(userId))
            return Ok(new { ok = false, message = "Thiếu userId." });

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var payload = await LoadUserPayloadAsync(conn, userId, ct);
        return Ok(new
        {
            ok = true,
            words = payload.Words,
            settings = payload.Settings
        });
    }

    private async Task<IActionResult> PushDataAsync(JsonElement body, CancellationToken ct)
    {
        string userId = GetString(body, "userId");
        if (string.IsNullOrWhiteSpace(userId))
            return Ok(new { ok = false, message = "Thiếu userId." });

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (body.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in wordsEl.EnumerateArray())
            {
                string wordId = GetString(item, "id");
                if (string.IsNullOrWhiteSpace(wordId))
                    wordId = Guid.NewGuid().ToString("N");

                string hanzi = GetString(item, "hanzi");
                string pinyin = GetString(item, "pinyin");
                string meaning = GetString(item, "meaning");
                string note = GetString(item, "note");
                long updatedAt = GetLong(item, "updatedAt", now);
                string rawJson = item.GetRawText();

                await using var upsertWord = new NpgsqlCommand(@"
                    INSERT INTO vocab_words (id, user_id, hanzi, pinyin, meaning, note, data_json, updated_at)
                    VALUES (@id, @user_id, @hanzi, @pinyin, @meaning, @note, @data_json::jsonb, @updated_at)
                    ON CONFLICT (id) DO UPDATE SET
                        hanzi = EXCLUDED.hanzi,
                        pinyin = EXCLUDED.pinyin,
                        meaning = EXCLUDED.meaning,
                        note = EXCLUDED.note,
                        data_json = EXCLUDED.data_json,
                        updated_at = EXCLUDED.updated_at
                    WHERE vocab_words.user_id = EXCLUDED.user_id", conn, tx);

                upsertWord.Parameters.AddWithValue("id", wordId);
                upsertWord.Parameters.AddWithValue("user_id", userId);
                upsertWord.Parameters.AddWithValue("hanzi", hanzi);
                upsertWord.Parameters.AddWithValue("pinyin", pinyin);
                upsertWord.Parameters.AddWithValue("meaning", meaning);
                upsertWord.Parameters.AddWithValue("note", note);
                upsertWord.Parameters.AddWithValue("data_json", rawJson);
                upsertWord.Parameters.AddWithValue("updated_at", updatedAt);
                await upsertWord.ExecuteNonQueryAsync(ct);
            }
        }

        if (body.TryGetProperty("deletedIds", out var delEl) && delEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var delItem in delEl.EnumerateArray())
            {
                string? delId = delItem.ValueKind == JsonValueKind.String ? delItem.GetString() : null;
                if (string.IsNullOrWhiteSpace(delId)) continue;

                await using var delCmd = new NpgsqlCommand(
                    "DELETE FROM vocab_words WHERE id = @id AND user_id = @user_id", conn, tx);
                delCmd.Parameters.AddWithValue("id", delId);
                delCmd.Parameters.AddWithValue("user_id", userId);
                await delCmd.ExecuteNonQueryAsync(ct);
            }
        }

        if (body.TryGetProperty("settings", out var settingsEl) && settingsEl.ValueKind == JsonValueKind.Object)
        {
            string settingsJson = settingsEl.GetRawText();
            await using var upsertSettings = new NpgsqlCommand(@"
                INSERT INTO user_settings (user_id, settings_json, updated_at)
                VALUES (@user_id, @settings_json::jsonb, @updated_at)
                ON CONFLICT (user_id) DO UPDATE SET
                    settings_json = EXCLUDED.settings_json,
                    updated_at = EXCLUDED.updated_at", conn, tx);

            upsertSettings.Parameters.AddWithValue("user_id", userId);
            upsertSettings.Parameters.AddWithValue("settings_json", settingsJson);
            upsertSettings.Parameters.AddWithValue("updated_at", now);
            await upsertSettings.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        var payload = await LoadUserPayloadAsync(conn, userId, ct);
        return Ok(new
        {
            ok = true,
            words = payload.Words,
            settings = payload.Settings
        });
    }

    private static async Task<(List<JsonElement> Words, JsonElement Settings)> LoadUserPayloadAsync(
        NpgsqlConnection conn, string userId, CancellationToken ct)
    {
        var words = new List<JsonElement>();

        await using (var wordsCmd = new NpgsqlCommand(@"
            SELECT data_json::text
            FROM vocab_words
            WHERE user_id = @user_id
            ORDER BY updated_at DESC", conn))
        {
            wordsCmd.Parameters.AddWithValue("user_id", userId);
            await using var reader = await wordsCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string jsonStr = reader.GetString(0);
                using var doc = JsonDocument.Parse(jsonStr);
                words.Add(doc.RootElement.Clone());
            }
        }

        JsonElement settingsElement;
        string settingsJson = "{}";

        await using (var settingsCmd = new NpgsqlCommand(@"
            SELECT settings_json::text
            FROM user_settings
            WHERE user_id = @user_id
            LIMIT 1", conn))
        {
            settingsCmd.Parameters.AddWithValue("user_id", userId);
            var obj = await settingsCmd.ExecuteScalarAsync(ct);
            if (obj is string s && !string.IsNullOrWhiteSpace(s))
                settingsJson = s;
        }

        using (var sDoc = JsonDocument.Parse(settingsJson))
        {
            settingsElement = sDoc.RootElement.Clone();
        }

        return (words, settingsElement);
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaInitialized) return;

        await _schemaLock.WaitAsync(ct);
        try
        {
            if (_schemaInitialized) return;

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = @"
                CREATE TABLE IF NOT EXISTS users (
                    id TEXT PRIMARY KEY,
                    username TEXT NOT NULL UNIQUE,
                    password_hash TEXT NOT NULL,
                    created_at BIGINT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS user_settings (
                    user_id TEXT PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
                    settings_json JSONB NOT NULL DEFAULT '{}'::jsonb,
                    updated_at BIGINT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS vocab_words (
                    id TEXT PRIMARY KEY,
                    user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    hanzi TEXT NOT NULL DEFAULT '',
                    pinyin TEXT NOT NULL DEFAULT '',
                    meaning TEXT NOT NULL DEFAULT '',
                    note TEXT NOT NULL DEFAULT '',
                    data_json JSONB NOT NULL DEFAULT '{}'::jsonb,
                    updated_at BIGINT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS cloud_kv (
                    ns TEXT NOT NULL,
                    item_key TEXT NOT NULL,
                    data_json JSONB NOT NULL DEFAULT 'null'::jsonb,
                    updated_at BIGINT NOT NULL,
                    PRIMARY KEY (ns, item_key)
                );

                CREATE INDEX IF NOT EXISTS idx_vocab_words_user_id ON vocab_words(user_id);";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);

            _schemaInitialized = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static string ExtractKey(JsonElement body)
    {
        string key = GetString(body, "key").Trim();
        if (!string.IsNullOrWhiteSpace(key)) return key;
        return GetString(body, "userId").Trim();
    }

    private static string ExtractPayloadJson(JsonElement body)
    {
        if (body.TryGetProperty("payload", out var p) &&
            p.ValueKind != JsonValueKind.Undefined &&
            p.ValueKind != JsonValueKind.Null)
        {
            return p.GetRawText();
        }
        return "{}";
    }

    private static string GetString(JsonElement el, string propName)
    {
        if (el.ValueKind != JsonValueKind.Object) return "";
        if (!el.TryGetProperty(propName, out var p)) return "";
        return p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "") : p.ToString();
    }

    private static long GetLong(JsonElement el, string propName, long fallback)
    {
        if (el.ValueKind != JsonValueKind.Object) return fallback;
        if (!el.TryGetProperty(propName, out var p)) return fallback;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var val)) return val;
        if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var parsed)) return parsed;
        return fallback;
    }

    private static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 2) return false;

        try
        {
            byte[] salt = Convert.FromBase64String(parts[0]);
            byte[] expectedHash = Convert.FromBase64String(parts[1]);
            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, 100_000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }
        catch
        {
            return false;
        }
    }
}
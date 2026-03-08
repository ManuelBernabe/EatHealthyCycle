using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EatHealthyCycle.Data;

namespace EatHealthyCycle.Controllers;

[ApiController]
[Route("api/db")]
[Authorize(Policy = "SuperUserMasterOnly")]
public class DbAdminController : ControllerBase
{
    private readonly AppDbContext _db;

    public DbAdminController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("tables")]
    public async Task<IActionResult> ListTables()
    {
        var conn = _db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EF%' ORDER BY name";
            using var reader = await cmd.ExecuteReaderAsync();
            var tables = new List<string>();
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));
            return Ok(tables);
        }
        finally { await conn.CloseAsync(); }
    }

    [HttpGet("tables/{tableName}")]
    public async Task<IActionResult> GetTableData(string tableName, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        // Validate table name to prevent SQL injection
        if (!System.Text.RegularExpressions.Regex.IsMatch(tableName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
            return BadRequest("Nombre de tabla inválido");

        var conn = _db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            // Get columns
            using var colCmd = conn.CreateCommand();
            colCmd.CommandText = $"PRAGMA table_info(\"{tableName}\")";
            using var colReader = await colCmd.ExecuteReaderAsync();
            var columns = new List<object>();
            while (await colReader.ReadAsync())
            {
                columns.Add(new
                {
                    name = colReader.GetString(1),
                    type = colReader.GetString(2),
                    pk = colReader.GetInt32(5) == 1
                });
            }
            await colReader.CloseAsync();

            // Get total count
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\"";
            var total = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

            // Get rows with pagination
            var offset = (page - 1) * pageSize;
            using var dataCmd = conn.CreateCommand();
            dataCmd.CommandText = $"SELECT * FROM \"{tableName}\" LIMIT {pageSize} OFFSET {offset}";
            using var dataReader = await dataCmd.ExecuteReaderAsync();

            var rows = new List<Dictionary<string, object?>>();
            while (await dataReader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < dataReader.FieldCount; i++)
                {
                    row[dataReader.GetName(i)] = dataReader.IsDBNull(i) ? null : dataReader.GetValue(i);
                }
                rows.Add(row);
            }

            return Ok(new { columns, rows, total, page, pageSize });
        }
        finally { await conn.CloseAsync(); }
    }

    [HttpPost("execute")]
    public async Task<IActionResult> ExecuteSql([FromBody] SqlRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Sql))
            return BadRequest("SQL vacío");

        var sql = request.Sql.Trim();

        // Block dangerous operations
        var upper = sql.ToUpperInvariant();
        if (upper.Contains("DROP DATABASE") || upper.Contains("ATTACH") || upper.Contains("DETACH"))
            return BadRequest("Operación no permitida");

        var conn = _db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            var isQuery = upper.StartsWith("SELECT") || upper.StartsWith("PRAGMA") || upper.StartsWith("EXPLAIN");

            if (isQuery)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                using var reader = await cmd.ExecuteReaderAsync();

                var columns = new List<string>();
                for (int i = 0; i < reader.FieldCount; i++)
                    columns.Add(reader.GetName(i));

                var rows = new List<Dictionary<string, object?>>();
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object?>();
                    for (int i = 0; i < reader.FieldCount; i++)
                        row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }

                return Ok(new { type = "query", columns, rows, rowCount = rows.Count });
            }
            else
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                var affected = await cmd.ExecuteNonQueryAsync();
                return Ok(new { type = "command", affected, message = $"{affected} filas afectadas" });
            }
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        finally { await conn.CloseAsync(); }
    }

    [HttpDelete("tables/{tableName}/{id}")]
    public async Task<IActionResult> DeleteRow(string tableName, int id)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(tableName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
            return BadRequest("Nombre de tabla inválido");

        var conn = _db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            // Find PK column
            using var colCmd = conn.CreateCommand();
            colCmd.CommandText = $"PRAGMA table_info(\"{tableName}\")";
            using var colReader = await colCmd.ExecuteReaderAsync();
            string? pkCol = null;
            while (await colReader.ReadAsync())
            {
                if (colReader.GetInt32(5) == 1)
                {
                    pkCol = colReader.GetString(1);
                    break;
                }
            }
            await colReader.CloseAsync();

            if (pkCol == null) return BadRequest("Tabla sin clave primaria");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM \"{tableName}\" WHERE \"{pkCol}\" = {id}";
            var affected = await cmd.ExecuteNonQueryAsync();
            return Ok(new { affected });
        }
        finally { await conn.CloseAsync(); }
    }
}

public record SqlRequest(string Sql);

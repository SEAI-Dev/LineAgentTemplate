using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace LineAgent.Api.Services;

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
    string DbType { get; }
}

public class DbConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;
    public string DbType { get; }

    public DbConnectionFactory(IConfiguration config)
    {
        DbType = Environment.GetEnvironmentVariable("LINEAGENT_DB")?.ToLower()
            ?? config["Database:Type"]?.ToLower()
            ?? "sqlite";
        _connectionString = DbType == "mssql"
            ? config.GetConnectionString("MSSQL")!
            : config.GetConnectionString("SQLite") ?? "Data Source=lineagent.db";
    }

    public IDbConnection CreateConnection()
    {
        return DbType == "mssql"
            ? new SqlConnection(_connectionString)
            : new SqliteConnection(_connectionString);
    }
}

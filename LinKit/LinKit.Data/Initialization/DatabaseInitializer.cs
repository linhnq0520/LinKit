using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LinKit.Data.Initialization;

public class DatabaseInitializer : IDatabaseInitializer
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly LinKitDataOptions _options;
    private readonly ILogger<DatabaseInitializer>? _logger;

    public DatabaseInitializer(
        IDbConnectionFactory connectionFactory,
        LinKitDataOptions options,
        ILogger<DatabaseInitializer>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _options = options;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!_options.EnsureDatabaseCreated && !_options.UserProvisioning.Enabled)
        {
            return;
        }

        // SQLite automatically creates file on connection open
        if (_options.Dialect == DatabaseDialect.Sqlite)
        {
            if (_options.EnsureDatabaseCreated)
            {
                await EnsureSqliteDatabaseCreatedAsync(ct);
            }
            return;
        }

        // Parse connection string to extract target database name and build system connection string
        string originalConnStr;
        using (var probeConn = _connectionFactory.CreateConnection())
        {
            originalConnStr = probeConn.ConnectionString;
        }

        if (string.IsNullOrWhiteSpace(originalConnStr))
        {
            _logger?.LogWarning("Connection string is empty or not provided. Skipping database initialization.");
            return;
        }

        var builder = new DbConnectionStringBuilder { ConnectionString = originalConnStr };
        var (dbKey, targetDbName) = ExtractDatabaseName(builder);

        if (string.IsNullOrWhiteSpace(targetDbName))
        {
            _logger?.LogDebug("No target database name specified in connection string. Skipping database initialization.");
            return;
        }

        if (_options.UserProvisioning.Enabled)
        {
            AutoFillUserProvisioningFromConnectionString(builder, _options.UserProvisioning);
        }

        switch (_options.Dialect)
        {
            case DatabaseDialect.SqlServer:
                await InitializeSqlServerAsync(builder, dbKey, targetDbName, ct);
                break;

            case DatabaseDialect.PostgreSql:
                await InitializePostgreSqlAsync(builder, dbKey, targetDbName, ct);
                break;

            case DatabaseDialect.MySql:
                await InitializeMySqlAsync(builder, dbKey, targetDbName, ct);
                break;

            case DatabaseDialect.Oracle:
                _logger?.LogInformation("Oracle dialect: Database creation is managed via PDB or Schema users.");
                break;
        }
    }

    private async Task<bool> CanConnectDirectlyAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await _connectionFactory.CreateConnectionAsync(ct);
            await conn.OpenAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureSqliteDatabaseCreatedAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await _connectionFactory.CreateConnectionAsync(ct);
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to verify SQLite database file creation.");
        }
    }

    #region SQL Server
    private async Task InitializeSqlServerAsync(
        DbConnectionStringBuilder builder,
        string? dbKey,
        string targetDbName,
        CancellationToken ct)
    {
        // Don't attempt to create system databases
        if (targetDbName.Equals("master", StringComparison.OrdinalIgnoreCase) ||
            targetDbName.Equals("tempdb", StringComparison.OrdinalIgnoreCase) ||
            targetDbName.Equals("model", StringComparison.OrdinalIgnoreCase) ||
            targetDbName.Equals("msdb", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Build master connection string (or use explicit AdminConnectionString if provided)
        string masterConnStr;
        if (!string.IsNullOrWhiteSpace(_options.AdminConnectionString))
        {
            masterConnStr = _options.AdminConnectionString;
        }
        else
        {
            var masterBuilder = new DbConnectionStringBuilder { ConnectionString = builder.ConnectionString };
            if (!string.IsNullOrEmpty(dbKey))
            {
                masterBuilder[dbKey] = "master";
            }
            else
            {
                masterBuilder["Database"] = "master";
            }
            masterConnStr = masterBuilder.ConnectionString;
        }

        // 1. Ensure Database Created
        if (_options.EnsureDatabaseCreated)
        {
            try
            {
                await using var sysConn = _connectionFactory.CreateConnection();
                sysConn.ConnectionString = masterConnStr;
                await sysConn.OpenAsync(ct);

                bool exists = false;
                await using (var checkCmd = sysConn.CreateCommand())
                {
                    checkCmd.CommandText = "SELECT 1 FROM sys.databases WHERE name = @dbName";
                    var p = checkCmd.CreateParameter();
                    p.ParameterName = "@dbName";
                    p.Value = targetDbName;
                    checkCmd.Parameters.Add(p);

                    var res = await checkCmd.ExecuteScalarAsync(ct);
                    exists = res != null && res != DBNull.Value;
                }

                if (!exists)
                {
                    _logger?.LogInformation("Creating SQL Server database '{Database}'...", targetDbName);
                    var sql = @"
DECLARE @sql NVARCHAR(MAX) = N'CREATE DATABASE ' + QUOTENAME(@dbName);
EXEC (@sql);";

                    await using var cmd = sysConn.CreateCommand();
                    cmd.CommandText = sql;
                    var param = cmd.CreateParameter();
                    param.ParameterName = "@dbName";
                    param.Value = targetDbName;
                    cmd.Parameters.Add(param);

                    await cmd.ExecuteNonQueryAsync(ct);
                    _logger?.LogInformation("SQL Server database '{Database}' created successfully.", targetDbName);

                    // Allow brief moment for SQL Server to set database to ONLINE status
                    await Task.Delay(300, ct);
                }
                else
                {
                    _logger?.LogDebug("SQL Server database '{Database}' already exists.", targetDbName);
                }
            }
            catch (Exception ex)
            {
                // Fallback check: if master was inaccessible (e.g. cloud restricted user), test direct connection
                if (await CanConnectDirectlyAsync(ct))
                {
                    _logger?.LogDebug("Direct connection to target database succeeded. Skipping master creation.");
                    return;
                }

                _logger?.LogError(ex, "Failed to auto-create SQL Server database '{Database}'.", targetDbName);
                throw new InvalidOperationException(
                    $"Failed to auto-create SQL Server database '{targetDbName}'. Please ensure connection credentials have appropriate permissions on 'master' or set options.AdminConnectionString. Error: {ex.Message}", ex);
            }
        }

        // 2. User Provisioning
        if (_options.UserProvisioning.Enabled && !string.IsNullOrWhiteSpace(_options.UserProvisioning.Username))
        {
            await ProvisionSqlServerUserAsync(masterConnStr, targetDbName, _options.UserProvisioning, ct);
        }
    }

    private async Task ProvisionSqlServerUserAsync(
        string masterConnStr,
        string targetDbName,
        DatabaseUserProvisioningOptions userOpt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userOpt.Username)) return;

        if (userOpt.Username.Equals("sa", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogDebug("User is 'sa' (sysadmin). Skipping user provisioning.");
            return;
        }

        try
        {
            _logger?.LogInformation("Provisioning SQL Server login and user '{Username}' for database '{Database}'...", userOpt.Username, targetDbName);

            // 1. Server Login on master
            await using (var sysConn = _connectionFactory.CreateConnection())
            {
                sysConn.ConnectionString = masterConnStr;
                await sysConn.OpenAsync(ct);

                var loginSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = @username)
BEGIN
    DECLARE @sqlLogin NVARCHAR(MAX) = N'CREATE LOGIN ' + QUOTENAME(@username) + N' WITH PASSWORD = ' + QUOTENAME(@password, '''') + N', CHECK_POLICY = OFF;';
    EXEC (@sqlLogin);
END";

                await using var cmd = sysConn.CreateCommand();
                cmd.CommandText = loginSql;

                var pUser = cmd.CreateParameter();
                pUser.ParameterName = "@username";
                pUser.Value = userOpt.Username;
                cmd.Parameters.Add(pUser);

                var pPass = cmd.CreateParameter();
                pPass.ParameterName = "@password";
                pPass.Value = userOpt.Password ?? "";
                cmd.Parameters.Add(pPass);

                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 2. User and Roles in target database
            await using (var targetConn = await _connectionFactory.CreateConnectionAsync(ct))
            {
                if (targetConn.State != System.Data.ConnectionState.Open)
                {
                    await targetConn.OpenAsync(ct);
                }

                var userSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @username)
BEGIN
    DECLARE @sqlUser NVARCHAR(MAX) = N'CREATE USER ' + QUOTENAME(@username) + N' FOR LOGIN ' + QUOTENAME(@username) + N';';
    EXEC (@sqlUser);
END";

                await using (var cmd = targetConn.CreateCommand())
                {
                    cmd.CommandText = userSql;
                    var pUser = cmd.CreateParameter();
                    pUser.ParameterName = "@username";
                    pUser.Value = userOpt.Username;
                    cmd.Parameters.Add(pUser);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                foreach (var role in userOpt.Roles ?? new[] { "db_owner" })
                {
                    if (string.IsNullOrWhiteSpace(role)) continue;

                    var roleSql = @"
DECLARE @sqlRole NVARCHAR(MAX) = N'ALTER ROLE ' + QUOTENAME(@role) + N' ADD MEMBER ' + QUOTENAME(@username) + N';';
EXEC (@sqlRole);";

                    await using var cmd = targetConn.CreateCommand();
                    cmd.CommandText = roleSql;

                    var pRole = cmd.CreateParameter();
                    pRole.ParameterName = "@role";
                    pRole.Value = role;
                    cmd.Parameters.Add(pRole);

                    var pUser = cmd.CreateParameter();
                    pUser.ParameterName = "@username";
                    pUser.Value = userOpt.Username;
                    cmd.Parameters.Add(pUser);

                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            _logger?.LogInformation("SQL Server user '{Username}' provisioned successfully with roles: [{Roles}].",
                userOpt.Username, string.Join(", ", userOpt.Roles ?? Array.Empty<string>()));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to provision SQL Server login/user '{Username}'.", userOpt.Username);
        }
    }
    #endregion

    #region PostgreSQL
    private async Task InitializePostgreSqlAsync(
        DbConnectionStringBuilder builder,
        string? dbKey,
        string targetDbName,
        CancellationToken ct)
    {
        if (targetDbName.Equals("postgres", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string systemConnStr;
        if (!string.IsNullOrWhiteSpace(_options.AdminConnectionString))
        {
            systemConnStr = _options.AdminConnectionString;
        }
        else
        {
            var pgBuilder = new DbConnectionStringBuilder { ConnectionString = builder.ConnectionString };
            if (!string.IsNullOrEmpty(dbKey))
            {
                pgBuilder[dbKey] = "postgres";
            }
            else
            {
                pgBuilder["Database"] = "postgres";
            }
            systemConnStr = pgBuilder.ConnectionString;
        }

        if (_options.EnsureDatabaseCreated)
        {
            try
            {
                await using var sysConn = _connectionFactory.CreateConnection();
                sysConn.ConnectionString = systemConnStr;
                await sysConn.OpenAsync(ct);

                bool exists = false;
                await using (var checkCmd = sysConn.CreateCommand())
                {
                    checkCmd.CommandText = "SELECT 1 FROM pg_database WHERE datname = @dbName;";
                    var p = checkCmd.CreateParameter();
                    p.ParameterName = "@dbName";
                    p.Value = targetDbName;
                    checkCmd.Parameters.Add(p);

                    var res = await checkCmd.ExecuteScalarAsync(ct);
                    exists = res != null && res != DBNull.Value;
                }

                if (!exists)
                {
                    _logger?.LogInformation("Creating PostgreSQL database '{Database}'...", targetDbName);
                    var sanitizedDb = targetDbName.Replace("\"", "\"\"");
                    await using var createCmd = sysConn.CreateCommand();
                    createCmd.CommandText = $"CREATE DATABASE \"{sanitizedDb}\";";
                    await createCmd.ExecuteNonQueryAsync(ct);
                    _logger?.LogInformation("PostgreSQL database '{Database}' created successfully.", targetDbName);
                    await Task.Delay(300, ct);
                }
                else
                {
                    _logger?.LogDebug("PostgreSQL database '{Database}' already exists.", targetDbName);
                }
            }
            catch (Exception ex)
            {
                if (await CanConnectDirectlyAsync(ct).ConfigureAwait(false))
                {
                    _logger?.LogDebug("Direct connection to target database succeeded. Skipping system creation.");
                    return;
                }

                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                {
                    _logger?.LogWarning("Auto-create PostgreSQL database '{Database}' was canceled.", targetDbName);
                    return;
                }

                _logger?.LogError(ex, "Failed to auto-create PostgreSQL database '{Database}'.", targetDbName);
                throw new InvalidOperationException(
                    $"Failed to auto-create PostgreSQL database '{targetDbName}'. Please ensure connection credentials have appropriate permissions on 'postgres' or set options.AdminConnectionString. Error: {ex.Message}", ex);
            }
        }

        if (_options.UserProvisioning.Enabled && !string.IsNullOrWhiteSpace(_options.UserProvisioning.Username))
        {
            await ProvisionPostgreSqlUserAsync(systemConnStr, targetDbName, _options.UserProvisioning, ct);
        }
    }

    private async Task ProvisionPostgreSqlUserAsync(
        string systemConnStr,
        string targetDbName,
        DatabaseUserProvisioningOptions userOpt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userOpt.Username)) return;

        if (userOpt.Username.Equals("postgres", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogDebug("User is 'postgres' (superuser). Skipping user provisioning.");
            return;
        }

        try
        {
            _logger?.LogInformation("Provisioning PostgreSQL role '{Username}' for database '{Database}'...", userOpt.Username, targetDbName);

            await using var sysConn = _connectionFactory.CreateConnection();
            sysConn.ConnectionString = systemConnStr;
            await sysConn.OpenAsync(ct);

            bool roleExists = false;
            await using (var checkCmd = sysConn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT 1 FROM pg_roles WHERE rolname = @username;";
                var p = checkCmd.CreateParameter();
                p.ParameterName = "@username";
                p.Value = userOpt.Username;
                checkCmd.Parameters.Add(p);
                var res = await checkCmd.ExecuteScalarAsync(ct);
                roleExists = res != null && res != DBNull.Value;
            }

            var sanitizedUser = userOpt.Username!.Replace("\"", "\"\"");
            var escapedPass = (userOpt.Password ?? "").Replace("'", "''");

            if (!roleExists)
            {
                await using var createCmd = sysConn.CreateCommand();
                createCmd.CommandText = $"CREATE ROLE \"{sanitizedUser}\" WITH LOGIN PASSWORD '{escapedPass}';";
                await createCmd.ExecuteNonQueryAsync(ct);
            }

            var sanitizedDb = targetDbName.Replace("\"", "\"\"");
            await using (var grantCmd = sysConn.CreateCommand())
            {
                grantCmd.CommandText = $"GRANT ALL PRIVILEGES ON DATABASE \"{sanitizedDb}\" TO \"{sanitizedUser}\";";
                await grantCmd.ExecuteNonQueryAsync(ct);
            }

            _logger?.LogInformation("PostgreSQL role '{Username}' provisioned successfully.", userOpt.Username);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to provision PostgreSQL role '{Username}'.", userOpt.Username);
        }
    }
    #endregion

    #region MySQL
    private async Task InitializeMySqlAsync(
        DbConnectionStringBuilder builder,
        string? dbKey,
        string targetDbName,
        CancellationToken ct)
    {
        string systemConnStr;
        if (!string.IsNullOrWhiteSpace(_options.AdminConnectionString))
        {
            systemConnStr = _options.AdminConnectionString;
        }
        else
        {
            var mySqlBuilder = new DbConnectionStringBuilder { ConnectionString = builder.ConnectionString };
            if (!string.IsNullOrEmpty(dbKey))
            {
                mySqlBuilder[dbKey] = "mysql";
            }
            else
            {
                mySqlBuilder["Database"] = "mysql";
            }
            systemConnStr = mySqlBuilder.ConnectionString;
        }

        if (_options.EnsureDatabaseCreated)
        {
            try
            {
                await using var sysConn = _connectionFactory.CreateConnection();
                sysConn.ConnectionString = systemConnStr;
                await sysConn.OpenAsync(ct);

                var sanitizedDb = targetDbName.Replace("`", "``");
                await using var cmd = sysConn.CreateCommand();
                cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{sanitizedDb}`;";
                await cmd.ExecuteNonQueryAsync(ct);
                _logger?.LogInformation("MySQL database '{Database}' is ready.", targetDbName);
                await Task.Delay(300, ct);
            }
            catch (Exception ex)
            {
                if (await CanConnectDirectlyAsync(ct))
                {
                    _logger?.LogDebug("Direct connection to target database succeeded. Skipping system creation.");
                    return;
                }

                _logger?.LogError(ex, "Failed to auto-create MySQL database '{Database}'.", targetDbName);
                throw new InvalidOperationException(
                    $"Failed to auto-create MySQL database '{targetDbName}'. Please ensure connection credentials have appropriate permissions or set options.AdminConnectionString. Error: {ex.Message}", ex);
            }
        }

        if (_options.UserProvisioning.Enabled && !string.IsNullOrWhiteSpace(_options.UserProvisioning.Username))
        {
            await ProvisionMySqlUserAsync(systemConnStr, targetDbName, _options.UserProvisioning, ct);
        }
    }

    private async Task ProvisionMySqlUserAsync(
        string systemConnStr,
        string targetDbName,
        DatabaseUserProvisioningOptions userOpt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userOpt.Username)) return;

        if (userOpt.Username.Equals("root", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogDebug("User is 'root' (superuser). Skipping user provisioning.");
            return;
        }

        try
        {
            _logger?.LogInformation("Provisioning MySQL user '{Username}' for database '{Database}'...", userOpt.Username, targetDbName);

            await using var sysConn = _connectionFactory.CreateConnection();
            sysConn.ConnectionString = systemConnStr;
            await sysConn.OpenAsync(ct);

            var sanitizedUser = userOpt.Username!.Replace("'", "''");
            var escapedPass = (userOpt.Password ?? "").Replace("'", "''");
            var sanitizedDb = targetDbName.Replace("`", "``");

            await using (var cmd = sysConn.CreateCommand())
            {
                cmd.CommandText = $"CREATE USER IF NOT EXISTS '{sanitizedUser}'@'%' IDENTIFIED BY '{escapedPass}';";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = sysConn.CreateCommand())
            {
                cmd.CommandText = $"GRANT ALL PRIVILEGES ON `{sanitizedDb}`.* TO '{sanitizedUser}'@'%'; FLUSH PRIVILEGES;";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            _logger?.LogInformation("MySQL user '{Username}' provisioned successfully.", userOpt.Username);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to provision MySQL user '{Username}'.", userOpt.Username);
        }
    }
    #endregion

    private static void AutoFillUserProvisioningFromConnectionString(DbConnectionStringBuilder builder, DatabaseUserProvisioningOptions userOpt)
    {
        if (string.IsNullOrWhiteSpace(userOpt.Username))
        {
            string[] userKeys = { "User Id", "UserId", "User", "UID", "Username", "username" };
            foreach (var k in userKeys)
            {
                if (builder.ContainsKey(k) && !string.IsNullOrWhiteSpace(builder[k]?.ToString()))
                {
                    userOpt.Username = builder[k]!.ToString();
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(userOpt.Password))
        {
            string[] pwdKeys = { "Password", "password", "PWD", "pwd" };
            foreach (var k in pwdKeys)
            {
                if (builder.ContainsKey(k) && !string.IsNullOrWhiteSpace(builder[k]?.ToString()))
                {
                    userOpt.Password = builder[k]!.ToString();
                    break;
                }
            }
        }
    }

    private static (string? dbKey, string? dbName) ExtractDatabaseName(DbConnectionStringBuilder builder)
    {
        string[] candidates = { "Database", "Initial Catalog", "database", "initial catalog", "DB", "db" };
        foreach (var key in candidates)
        {
            if (builder.ContainsKey(key))
            {
                var val = builder[key]?.ToString();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    return (key, val);
                }
            }
        }
        return (null, null);
    }
}

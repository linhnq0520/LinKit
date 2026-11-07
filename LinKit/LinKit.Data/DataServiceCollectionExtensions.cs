using System;
using System.Data;
using System.Data.Common;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Data.Initialization;
using LinKit.Data.Synchronization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinKit.Data;

/// <summary>
/// Ambient accessor for compiled JsonSerializerContext across LinKit.Data repositories.
/// Use a generated context for JSON column serialization in trimmed and Native AOT applications.
/// </summary>
public static class LinKitDataJsonSerializerAccessor
{
    public static JsonSerializerContext? Context { get; set; }
}

public class DatabaseUserProvisioningOptions
{
    public bool Enabled { get; set; } = false;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string[] Roles { get; set; } = new[] { "db_owner" };
}

public class LinKitDataOptions
{
    /// <summary>Default upper bound on rows per generated range statement.</summary>
    public const int DefaultBatchSize = 1000;

    /// <summary>Default per-command timeout, in seconds.</summary>
    public const int DefaultCommandTimeout = 30;

    public DatabaseDialect Dialect { get; set; } = DatabaseDialect.Sqlite;
    public Func<DbConnection>? ConnectionCreator { get; set; }
    public Func<IServiceProvider, string?>? TenantIdResolver { get; private set; }
    public Func<DbCommand, ValueTask>? CommandConfigurator { get; private set; }
    public bool EnsureDatabaseCreated { get; set; } = true;
    public string? AdminConnectionString { get; set; }
    public DatabaseUserProvisioningOptions UserProvisioning { get; } = new();
    public bool AutoSyncSchema { get; set; } = true;
    public bool RunMigrations { get; set; } = true;
    public bool RunDataMigrations
    {
        get => RunMigrations;
        set => RunMigrations = value;
    }
    public JsonSerializerContext? JsonSerializerContext { get; set; }

    /// <summary>
    /// Upper bound on the rows a single generated range statement carries. It is further clamped per statement
    /// by the provider parameter limit, so raising it cannot exceed what the provider accepts.
    /// </summary>
    public int BatchSize { get; private set; } = DefaultBatchSize;

    /// <summary>Per-command timeout in seconds; 0 disables the limit (provider default applies).</summary>
    public int CommandTimeout { get; private set; } = DefaultCommandTimeout;

    /// <summary>Isolation level applied when a transaction is started without an explicit one.</summary>
    public IsolationLevel TransactionIsolation { get; private set; } = IsolationLevel.Unspecified;

    /// <summary>
    /// Serializes concurrent operations on one <see cref="DataSession"/> on a single connection instead of throwing.
    /// Enable this when handlers issue parallel queries inside one request scope.
    /// </summary>
    public bool ConcurrentOperations { get; private set; }

    /// <summary>Policy applied to idempotent commands (queries and explicit <c>*Idempotent*</c> calls only).</summary>
    public IDataOperationExecutor? IdempotentExecutor { get; private set; }

    public LinKitDataOptions UseConnection(
        DatabaseDialect dialect,
        Func<DbConnection> connectionCreator
    )
    {
        Dialect = dialect;
        ConnectionCreator =
            connectionCreator ?? throw new ArgumentNullException(nameof(connectionCreator));
        return this;
    }

    public LinKitDataOptions UseTenantId(Func<IServiceProvider, string?> tenantIdResolver)
    {
        TenantIdResolver =
            tenantIdResolver ?? throw new ArgumentNullException(nameof(tenantIdResolver));
        return this;
    }

    public LinKitDataOptions ConfigureCommand(Func<DbCommand, ValueTask> commandConfigurator)
    {
        CommandConfigurator =
            commandConfigurator ?? throw new ArgumentNullException(nameof(commandConfigurator));
        return this;
    }

    public LinKitDataOptions UseBatchSize(int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        BatchSize = batchSize;
        return this;
    }

    /// <summary>Sets the per-command timeout in seconds; 0 disables the limit.</summary>
    public LinKitDataOptions UseCommandTimeout(int seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        CommandTimeout = seconds;
        return this;
    }

    /// <summary>Sets the isolation level used for transactions started without an explicit one.</summary>
    public LinKitDataOptions UseTransactionIsolation(IsolationLevel isolation)
    {
        TransactionIsolation = isolation;
        return this;
    }

    /// <summary>Serializes concurrent operations on one session instead of throwing on overlap.</summary>
    public LinKitDataOptions UseConcurrentOperations(bool allow = true)
    {
        ConcurrentOperations = allow;
        return this;
    }

    /// <summary>Retries idempotent commands when <paramref name="isTransient"/> reports a transient failure.</summary>
    public LinKitDataOptions UseTransientFailureRetry(
        Func<Exception, bool> isTransient,
        int maxRetries = 3,
        TimeSpan? baseDelay = null
    )
    {
        IdempotentExecutor = new TransientFailureExecutor(isTransient, maxRetries, baseDelay);
        return this;
    }

    /// <summary>
    /// Installs a custom policy for idempotent commands, for example an adapter over
    /// <c>LinKit.Resilience.ResiliencePipeline</c> (retry + circuit breaker + per-attempt timeout).
    /// </summary>
    public LinKitDataOptions UseIdempotentExecutor(IDataOperationExecutor executor)
    {
        IdempotentExecutor = executor ?? throw new ArgumentNullException(nameof(executor));
        return this;
    }

    public LinKitDataOptions UseAdminConnectionString(string adminConnectionString)
    {
        AdminConnectionString = adminConnectionString;
        return this;
    }

    public LinKitDataOptions ConfigureUserProvisioning(
        Action<DatabaseUserProvisioningOptions> configure
    )
    {
        configure?.Invoke(UserProvisioning);
        return this;
    }

    public LinKitDataOptions UseJsonSerializerContext(JsonSerializerContext context)
    {
        JsonSerializerContext = context ?? throw new ArgumentNullException(nameof(context));
        LinKitDataJsonSerializerAccessor.Context = context;
        return this;
    }
}

public class DataAutoSyncHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LinKitDataOptions _options;
    private readonly ILogger<DataAutoSyncHostedService>? _logger;

    public DataAutoSyncHostedService(
        IServiceProvider serviceProvider,
        LinKitDataOptions options,
        ILogger<DataAutoSyncHostedService>? logger = null
    )
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (
            !_options.EnsureDatabaseCreated
            && !_options.UserProvisioning.Enabled
            && !_options.AutoSyncSchema
            && !_options.RunMigrations
        )
        {
            return;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();

            // 0. Ensure Database & User Provisioning
            if (_options.EnsureDatabaseCreated || _options.UserProvisioning.Enabled)
            {
                var initializer = scope.ServiceProvider.GetService<IDatabaseInitializer>();
                if (initializer != null)
                {
                    await initializer.InitializeAsync(cancellationToken);
                }
            }

            // 1. Schema DDL auto-sync
            if (_options.AutoSyncSchema)
            {
                var synchronizer = scope.ServiceProvider.GetService<ISchemaSynchronizer>();
                if (synchronizer != null)
                {
                    await synchronizer.SynchronizeAsync(cancellationToken);
                }
            }

            // 2. Versioned Data & Schema migrations
            if (_options.RunMigrations)
            {
                var runner =
                    scope.ServiceProvider.GetService<LinKit.Data.Migrations.IMigrationRunner>();
                if (runner != null)
                {
                    await runner.RunMigrationsAsync(cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "Error occurred during LinKit.Data Auto-Schema or Data Migration initialization at startup."
            );
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class EmptyMigrationRegistry : LinKit.Data.Migrations.IMigrationRegistry
{
    public IReadOnlyList<LinKit.Data.Migrations.MigrationDescriptor> GetMigrations() =>
        Array.Empty<LinKit.Data.Migrations.MigrationDescriptor>();
}

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddLinKitDataCore(
        this IServiceCollection services,
        Action<LinKitDataOptions> configure
    )
    {
        var options = new LinKitDataOptions();
        configure(options);

        if (options.ConnectionCreator == null)
        {
            throw new InvalidOperationException(
                "ConnectionCreator must be specified via options.UseConnection(...)."
            );
        }

        if (options.JsonSerializerContext != null)
        {
            LinKitDataJsonSerializerAccessor.Context = options.JsonSerializerContext;
        }

        services.AddSingleton(options);
        services.AddSingleton<IDbConnectionFactory>(
            new DelegateDbConnectionFactory(options.Dialect, options.ConnectionCreator)
        );
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<DataSession>(sp =>
        {
            var opt = sp.GetRequiredService<LinKitDataOptions>();
            return new DataSession(
                opt.ConnectionCreator!,
                new SqlDialect(opt.Dialect),
                opt.TenantIdResolver?.Invoke(sp),
                opt.CommandConfigurator
            )
            {
                SharedUnitOfWork = sp.GetRequiredService<IUnitOfWork>() as UnitOfWork,
                BatchSize = opt.BatchSize,
                CommandTimeout = opt.CommandTimeout,
                DefaultIsolationLevel = opt.TransactionIsolation,
                AllowConcurrentOperations = opt.ConcurrentOperations,
                IdempotentExecutor = opt.IdempotentExecutor,
            };
        });
        services.AddScoped<IDatabaseInitializer, DatabaseInitializer>();
        services.AddScoped<ISchemaSynchronizer, SchemaSynchronizer>();
        services.AddScoped<
            LinKit.Data.Migrations.IMigrationRunner,
            LinKit.Data.Migrations.MigrationRunner
        >();

        // Default empty migration registry if not supplied by Source Generator
        services.AddSingleton<LinKit.Data.Migrations.IMigrationRegistry, EmptyMigrationRegistry>();

        if (
            options.EnsureDatabaseCreated
            || options.UserProvisioning.Enabled
            || options.AutoSyncSchema
            || options.RunDataMigrations
        )
        {
            services.AddHostedService<DataAutoSyncHostedService>();
        }

        return services;
    }
}

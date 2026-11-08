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
    /// <summary>Context used for JSON columns when a repository has no explicit context; setting it here applies process wide.</summary>
    public static JsonSerializerContext? Context { get; set; }
}

/// <summary>
/// Options for creating the database and its application user through the administrative connection at startup.
/// </summary>
public class DatabaseUserProvisioningOptions
{
    /// <summary>Whether provisioning runs during startup; defaults to <see langword="false"/>.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Login name of the application user to create.</summary>
    public string? Username { get; set; }

    /// <summary>Password of the application user to create.</summary>
    public string? Password { get; set; }

    /// <summary>Roles or privileges granted to the application user; defaults to <c>db_owner</c>.</summary>
    public string[] Roles { get; set; } = new[] { "db_owner" };
}

/// <summary>
/// Configuration of LinKit.Data: the connection factory, dialect, tenant resolution, batching and startup behavior.
/// </summary>
public class LinKitDataOptions
{
    /// <summary>Default upper bound on rows per generated range statement.</summary>
    public const int DefaultBatchSize = 1000;

    /// <summary>Default per-command timeout, in seconds.</summary>
    public const int DefaultCommandTimeout = 30;

    /// <summary>Database dialect used for SQL generation; defaults to <see cref="DatabaseDialect.Sqlite"/>.</summary>
    public DatabaseDialect Dialect { get; set; } = DatabaseDialect.Sqlite;

    /// <summary>Creates a new, unopened connection; set it through <see cref="UseConnection"/>.</summary>
    public Func<DbConnection>? ConnectionCreator { get; set; }

    /// <summary>Resolves the current tenant from the request provider; every tenant-scoped query is filtered by it.</summary>
    public Func<IServiceProvider, string?>? TenantIdResolver { get; private set; }

    /// <summary>Runs for every command just before execution, for example to attach provider-specific hints.</summary>
    public Func<DbCommand, ValueTask>? CommandConfigurator { get; private set; }

    /// <summary>Whether startup creates the database when it does not exist; defaults to <see langword="true"/>.</summary>
    public bool EnsureDatabaseCreated { get; set; } = true;

    /// <summary>Administrative connection string used by database and user provisioning.</summary>
    public string? AdminConnectionString { get; set; }

    /// <summary>Application user provisioning applied at startup.</summary>
    public DatabaseUserProvisioningOptions UserProvisioning { get; } = new();

    /// <summary>Whether startup reconciles the schema with the generated metadata; defaults to <see langword="true"/>.</summary>
    public bool AutoSyncSchema { get; set; } = true;

    /// <summary>Whether startup runs the registered migrations; defaults to <see langword="true"/>.</summary>
    public bool RunMigrations { get; set; } = true;

    /// <summary>Alias of <see cref="RunMigrations"/> kept for readability.</summary>
    public bool RunDataMigrations
    {
        get => RunMigrations;
        set => RunMigrations = value;
    }

    /// <summary>Source-generated JSON context used for JSON columns in trimmed and Native AOT applications.</summary>
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

    /// <summary>Selects the dialect and supplies the factory used to open connections.</summary>
    /// <param name="dialect">Database dialect used for SQL generation.</param>
    /// <param name="connectionCreator">Creates a new, unopened connection on each call.</param>
    /// <returns>The same options instance, to allow chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connectionCreator"/> is <see langword="null"/>.</exception>
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

    /// <summary>Registers the delegate that resolves the current tenant for every tenant-scoped query.</summary>
    /// <param name="tenantIdResolver">Resolves the tenant from the request service provider; <see langword="null"/> means no tenant.</param>
    /// <returns>The same options instance, to allow chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tenantIdResolver"/> is <see langword="null"/>.</exception>
    public LinKitDataOptions UseTenantId(Func<IServiceProvider, string?> tenantIdResolver)
    {
        TenantIdResolver =
            tenantIdResolver ?? throw new ArgumentNullException(nameof(tenantIdResolver));
        return this;
    }

    /// <summary>Registers a callback that runs on every command just before execution.</summary>
    /// <param name="commandConfigurator">Callback used to attach provider-specific hints or instrumentation.</param>
    /// <returns>The same options instance, to allow chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="commandConfigurator"/> is <see langword="null"/>.</exception>
    public LinKitDataOptions ConfigureCommand(Func<DbCommand, ValueTask> commandConfigurator)
    {
        CommandConfigurator =
            commandConfigurator ?? throw new ArgumentNullException(nameof(commandConfigurator));
        return this;
    }

    /// <summary>Sets the upper bound on rows per generated range statement.</summary>
    /// <param name="batchSize">Positive row limit; it is still clamped by the provider parameter limit.</param>
    /// <returns>The same options instance, to allow chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="batchSize"/> is zero or negative.</exception>
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

    /// <summary>Sets the connection string used for administrative tasks such as creating the database or its user.</summary>
    /// <param name="adminConnectionString">Connection string for an account allowed to issue DDL on the server.</param>
    /// <returns>The same options instance, to allow chaining.</returns>
    public LinKitDataOptions UseAdminConnectionString(string adminConnectionString)
    {
        AdminConnectionString = adminConnectionString;
        return this;
    }

    /// <summary>Configures the application user provisioned at startup.</summary>
    /// <param name="configure">Callback that edits <see cref="UserProvisioning"/>.</param>
    /// <returns>The same options instance, to allow chaining.</returns>
    public LinKitDataOptions ConfigureUserProvisioning(
        Action<DatabaseUserProvisioningOptions> configure
    )
    {
        configure?.Invoke(UserProvisioning);
        return this;
    }

    /// <summary>Registers the source-generated JSON context used for JSON columns.</summary>
    /// <param name="context">Generated context that contains metadata for every serialized property type.</param>
    /// <returns>The same options instance, to allow chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public LinKitDataOptions UseJsonSerializerContext(JsonSerializerContext context)
    {
        JsonSerializerContext = context ?? throw new ArgumentNullException(nameof(context));
        LinKitDataJsonSerializerAccessor.Context = context;
        return this;
    }
}

/// <summary>
/// Runs the startup pipeline in a dedicated scope: database and user provisioning, schema synchronization, then
/// versioned migrations.
/// </summary>
/// <remarks>
/// Each stage is skipped when the matching option is disabled, and a failure stops the host from starting so schema
/// drift is never served silently.
/// </remarks>
public class DataAutoSyncHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LinKitDataOptions _options;
    private readonly ILogger<DataAutoSyncHostedService>? _logger;

    /// <summary>Creates the hosted service.</summary>
    /// <param name="serviceProvider">Root provider used to create the startup scope.</param>
    /// <param name="options">Options that decide which startup stages run.</param>
    /// <param name="logger">Optional logger that records a startup failure.</param>
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

    /// <summary>Executes the enabled startup stages.</summary>
    /// <param name="cancellationToken">Token used to cancel the startup work.</param>
    /// <returns>A task that completes when the pipeline finishes.</returns>
    /// <exception cref="Exception">A stage failed; the failure is logged and rethrown to stop the host.</exception>
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

    /// <summary>Performs no work; the startup pipeline has already completed.</summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Migration registry used when the source generator did not emit one, so the runner can still be resolved.
/// </summary>
public sealed class EmptyMigrationRegistry : LinKit.Data.Migrations.IMigrationRegistry
{
    /// <summary>Returns an empty list.</summary>
    /// <returns>An empty migration list.</returns>
    public IReadOnlyList<LinKit.Data.Migrations.MigrationDescriptor> GetMigrations() =>
        Array.Empty<LinKit.Data.Migrations.MigrationDescriptor>();
}

/// <summary>
/// Registers LinKit.Data services with the dependency injection container.
/// </summary>
public static class DataServiceCollectionExtensions
{
    /// <summary>Registers the session, unit of work, initializer, schema synchronizer and migration runner.</summary>
    /// <param name="services">Service collection to extend.</param>
    /// <param name="configure">Callback that configures <see cref="LinKitDataOptions"/>; it must call <see cref="LinKitDataOptions.UseConnection"/>.</param>
    /// <returns>The same service collection, to allow chaining.</returns>
    /// <exception cref="InvalidOperationException">No connection creator was configured.</exception>
    /// <remarks>
    /// The startup hosted service is registered only when at least one of database creation, user provisioning, schema
    /// synchronization or migrations is enabled.
    /// </remarks>
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

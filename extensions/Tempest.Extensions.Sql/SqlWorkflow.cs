using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Tempest.Domain.Execution;
using Tempest.Domain.Metrics;

namespace Tempest.Extensions.Sql;

/// <summary>
/// Protocole de reference SQL de la roadmap phase 6 : un <see cref="IWorkflow"/> qui interroge une
/// vraie base SQLite plutot que <see cref="IVirtualUserContext.HttpClient"/> — la preuve que le
/// contrat de plugin (<c>IWorkflow</c>/<c>IVirtualUserContext</c>/<c>StepScope</c>) tient pour un
/// protocole reellement different de HTTP, pas une variation de plus autour du client partage.
/// <para>
/// SQLite plutot qu'un serveur SQL (PostgreSQL, SQL Server...) : base embarquee, aucune
/// infrastructure supplementaire a demarrer pour verifier ce chantier de bout en bout — le champ
/// "SQL" de la roadmap etait deja explicitement ecarte des jeux de donnees (phase 2) pour cette
/// meme raison de scope, ce protocole de reference le referme.
/// </para>
/// <para>
/// Limite assumee, comme <c>Tempest.SamplePlugin</c> : aucune configuration injectee par Tempest,
/// ce plugin lit la sienne (chemin de la base, nombre de lignes de reference) depuis des variables
/// d'environnement.
/// </para>
/// <para>
/// Trouvaille reelle en verifiant ce chantier de bout en bout, documentee ici plutot que corrigee
/// dans le cœur : un plugin charge par <c>Assembly.LoadFrom</c> doit etre <b>publie</b>
/// (<c>dotnet publish</c>), pas seulement compile — <c>dotnet build</c> seul ne copie pas les
/// dependances NuGet transitives a cote de l'assembly, que <c>PluginWorkflowLoader</c> ne resout
/// alors plus. Publier suffit pour une dependance geree (<c>Microsoft.Data.Sqlite.dll</c> a cote de
/// l'assembly), mais pas pour sa bibliotheque native (<c>e_sqlite3</c>) : <c>SQLitePCLRaw</c> la
/// cherche par defaut a cote de l'assembly <b>hote</b> (<c>Tempest.Cli</c>), qui ne l'a jamais
/// referencee, pas a cote du plugin. Le constructeur statique ci-dessous enregistre un
/// <see cref="NativeLibrary.SetDllImportResolver"/> qui la cherche a la place a cote de <b>cette</b>
/// assembly — un plugin qui embarque une dependance native doit faire de meme, ce n'est pas une
/// limite du contrat lui-meme.
/// </para>
/// </summary>
public sealed class SqlWorkflow : IWorkflow
{
    private const string CONNECTION_STRING_ENVIRONMENT_VARIABLE = "TEMPEST_SQL_PLUGIN_CONNECTION_STRING";
    private const string ROW_COUNT_ENVIRONMENT_VARIABLE = "TEMPEST_SQL_PLUGIN_ROW_COUNT";
    private const int DEFAULT_ROW_COUNT = 1_000;
    private const string NATIVE_SQLITE_LIBRARY_NAME = "e_sqlite3";

    private readonly string _connectionString;
    private readonly int _rowCount;

    private StepId _selectStep;
    private StepId _insertStep;

    static SqlWorkflow()
    {
        // Le P/Invoke vers e_sqlite3 est declare dans SQLitePCLRaw.provider.e_sqlite3, pas dans
        // Microsoft.Data.Sqlite : NativeLibrary.SetDllImportResolver ne s'applique qu'a l'assembly
        // qui declare l'appel natif, pas a celle qui l'utilise indirectement — decouvert en
        // verifiant ce chantier de bout en bout (le resolveur enregistre sur la mauvaise assembly
        // n'etait jamais invoque, echec identique a l'absence de resolveur).
        Assembly? providerAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(static assembly => assembly.GetName().Name == "SQLitePCLRaw.provider.e_sqlite3")
            ?? TryLoadProviderAssembly();

        if (providerAssembly is not null)
        {
            NativeLibrary.SetDllImportResolver(providerAssembly, ResolveNativeSqliteLibrary);
        }
    }

    private static Assembly? TryLoadProviderAssembly()
    {
        try
        {
            return Assembly.Load("SQLitePCLRaw.provider.e_sqlite3");
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    public SqlWorkflow()
    {
        string defaultPath = Path.Combine(Path.GetTempPath(), "tempest-sql-plugin.db");
        _connectionString = Environment.GetEnvironmentVariable(CONNECTION_STRING_ENVIRONMENT_VARIABLE) is { Length: > 0 } configured
            ? configured
            : $"Data Source={defaultPath}";

        _rowCount = Environment.GetEnvironmentVariable(ROW_COUNT_ENVIRONMENT_VARIABLE) is { Length: > 0 } configuredRowCount
            && int.TryParse(configuredRowCount, out int parsed) && parsed > 0
            ? parsed
            : DEFAULT_ROW_COUNT;
    }

    /// <inheritdoc />
    public string Name => "sql-plugin";

    /// <inheritdoc />
    public void RegisterSteps(StepRegistry registry)
    {
        _selectStep = registry.Register("SQL SELECT product");
        _insertStep = registry.Register("SQL INSERT order");
    }

    /// <summary>
    /// Cree le schema s'il n'existe pas encore et seme <see cref="_rowCount"/> produits de
    /// reference — graine fixe (identifiants 1..N), pour qu'un tir soit reproductible d'une
    /// execution a l'autre, comme <c>DynamicCheckoutWorkflow</c>.
    /// <para>
    /// Active le mode WAL : autorise des lecteurs concurrents pendant une ecriture, indispensable
    /// des que plusieurs utilisateurs virtuels partagent le meme fichier SQLite en meme temps.
    /// </para>
    /// </summary>
    public async ValueTask SetUpAsync(CancellationToken cancellationToken)
    {
        using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS products (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                price REAL NOT NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS orders (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                product_id INTEGER NOT NULL,
                quantity INTEGER NOT NULL,
                created_at_ticks INTEGER NOT NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);

        long existingCount = (long)(await new SqliteCommand("SELECT COUNT(*) FROM products;", connection)
            .ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (existingCount >= _rowCount)
        {
            return;
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        for (int id = 1; id <= _rowCount; id++)
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO products (id, name, price) VALUES (@id, @name, @price);";
            insert.Parameters.AddWithValue("@id", id);
            insert.Parameters.AddWithValue("@name", $"product-{id}");
            insert.Parameters.AddWithValue("@price", Math.Round(id * 1.5, 2));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        int productId = Random.Shared.Next(1, _rowCount + 1);

        await SelectProductAsync(context, productId, cancellationToken).ConfigureAwait(false);
        await InsertOrderAsync(context, productId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SelectProductAsync(IVirtualUserContext context, int productId, CancellationToken cancellationToken)
    {
        StepScope scope = context.BeginStep(_selectStep);

        try
        {
            using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT name, price FROM products WHERE id = @id;";
            command.Parameters.AddWithValue("@id", productId);

            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Un identifiant tire dans [1, rowCount] doit toujours exister apres SetUpAsync :
                // son absence est une assertion metier ratee, pas un incident de transport.
                scope.Fail(RequestOutcome.AssertionFailed);
                return;
            }

            scope.Success();
        }
        catch (SqliteException ex)
        {
            scope.Fail(RequestOutcome.ConnectionError, ex.SqliteErrorCode);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            scope.Fail(RequestOutcome.Timeout);
        }
    }

    private async ValueTask InsertOrderAsync(IVirtualUserContext context, int productId, CancellationToken cancellationToken)
    {
        StepScope scope = context.BeginStep(_insertStep);

        try
        {
            using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO orders (product_id, quantity, created_at_ticks) VALUES (@productId, @quantity, @ticks);";
            command.Parameters.AddWithValue("@productId", productId);
            command.Parameters.AddWithValue("@quantity", Random.Shared.Next(1, 4));
            command.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);

            int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected != 1)
            {
                scope.Fail(RequestOutcome.AssertionFailed);
                return;
            }

            scope.Success();
        }
        catch (SqliteException ex)
        {
            scope.Fail(RequestOutcome.ConnectionError, ex.SqliteErrorCode);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            scope.Fail(RequestOutcome.Timeout);
        }
    }

    /// <summary>
    /// Ouvre une connexion et regle son delai d'attente sous verrou : SQLite n'autorise qu'un seul
    /// ecrivain a la fois meme en WAL, ce delai evite qu'un utilisateur virtuel echoue
    /// immediatement sur une ecriture concurrente plutot que d'attendre brievement son tour.
    /// </summary>
    private async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cherche <c>e_sqlite3</c> a cote de <b>cette</b> assembly (<c>runtimes/&lt;rid&gt;/native/...</c>,
    /// exactement l'arborescence qu'y depose <c>dotnet publish</c>) plutot que de laisser
    /// <c>SQLitePCLRaw</c> ne chercher qu'a cote de l'assembly hote — voir la remarque de classe.
    /// </summary>
    private static IntPtr ResolveNativeSqliteLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != NATIVE_SQLITE_LIBRARY_NAME)
        {
            return IntPtr.Zero;
        }

        string? pluginDirectory = Path.GetDirectoryName(typeof(SqlWorkflow).Assembly.Location);
        if (pluginDirectory is null)
        {
            return IntPtr.Zero;
        }

        string fileName = OperatingSystem.IsWindows()
            ? $"{NATIVE_SQLITE_LIBRARY_NAME}.dll"
            : OperatingSystem.IsMacOS()
                ? $"lib{NATIVE_SQLITE_LIBRARY_NAME}.dylib"
                : $"lib{NATIVE_SQLITE_LIBRARY_NAME}.so";

        string nativePath = Path.Combine(pluginDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", fileName);

        return File.Exists(nativePath) && NativeLibrary.TryLoad(nativePath, out IntPtr handle) ? handle : IntPtr.Zero;
    }
}
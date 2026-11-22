using Sakana.Application;
using Sakana.Domain;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Sakana.Infrastructure;

public sealed class SqliteLauncherStore : ILauncherStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public SqliteLauncherStore()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        var sakanaDataDirectory = Path.Combine(localAppData, "Sakana");
        var legacyAuroraDataDirectory = Path.Combine(localAppData, "Aurora");
        var sakanaDatabasePath = Path.Combine(sakanaDataDirectory, "Sakana.db");
        var legacyAuroraDatabasePath = Path.Combine(legacyAuroraDataDirectory, "Aurora.db");

        // Prefer the renamed product's database, but keep existing Aurora installs usable.
        DataDirectory = File.Exists(sakanaDatabasePath) || !File.Exists(legacyAuroraDatabasePath)
            ? sakanaDataDirectory
            : legacyAuroraDataDirectory;
        var databasePath = DataDirectory == legacyAuroraDataDirectory
            ? legacyAuroraDatabasePath
            : sakanaDatabasePath;

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public string DataDirectory { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(Path.Combine(DataDirectory, "instances"));
            Directory.CreateDirectory(Path.Combine(DataDirectory, "shared", "assets"));
            Directory.CreateDirectory(Path.Combine(DataDirectory, "shared", "libraries"));
            Directory.CreateDirectory(Path.Combine(DataDirectory, "runtimes"));
            Directory.CreateDirectory(Path.Combine(DataDirectory, "downloads"));
            Directory.CreateDirectory(Path.Combine(DataDirectory, "logs"));

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL;";
            await command.ExecuteNonQueryAsync(cancellationToken);

            command.CommandText = "PRAGMA user_version;";
            var schemaVersion = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            if (schemaVersion > 12)
            {
                throw new InvalidOperationException($"Sakana database version {schemaVersion} is newer than this app supports.");
            }

            if (schemaVersion == 0)
            {
                command.CommandText = """
                    BEGIN IMMEDIATE;
                CREATE TABLE IF NOT EXISTS AppSettings (
                    Key TEXT NOT NULL PRIMARY KEY,
                    Value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS InstanceProfiles (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    VersionId TEXT NOT NULL,
                    State TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    LastError TEXT NULL
                );
                INSERT OR IGNORE INTO AppSettings (Key, Value) VALUES ('Theme', 'Dark');
                PRAGMA user_version = 2;
                COMMIT;
                """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 2;
            }

            if (schemaVersion == 1)
            {
                command.CommandText = """
                    BEGIN IMMEDIATE;
                    ALTER TABLE InstanceProfiles ADD COLUMN LastError TEXT NULL;
                    PRAGMA user_version = 2;
                    COMMIT;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 2;
            }

            if (schemaVersion == 2)
            {
                command.CommandText = """
                    BEGIN IMMEDIATE;
                    UPDATE InstanceProfiles
                    SET State = 'Failed', LastError = 'The matching Java runtime has not been installed yet. Retry this installation.'
                    WHERE State = 'Installed';
                    PRAGMA user_version = 3;
                    COMMIT;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 3;
            }

            if (schemaVersion == 3)
            {
                command.CommandText = """
                    BEGIN IMMEDIATE;
                    CREATE TABLE LaunchHistory (
                        Id TEXT NOT NULL PRIMARY KEY,
                        InstanceId TEXT NOT NULL,
                        VersionId TEXT NOT NULL,
                        StartedAtUtc TEXT NOT NULL,
                        EndedAtUtc TEXT NULL,
                        ExitCode INTEGER NULL
                    );
                    CREATE INDEX IX_LaunchHistory_StartedAtUtc ON LaunchHistory (StartedAtUtc DESC);
                    PRAGMA user_version = 4;
                    COMMIT;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 4;
            }

            if (schemaVersion == 4)
            {
                command.CommandText = """
                    BEGIN IMMEDIATE;
                    PRAGMA user_version = 5;
                    COMMIT;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 5;
            }

            if (schemaVersion == 5)
            {
                command.CommandText = """
                    BEGIN IMMEDIATE;
                    ALTER TABLE InstanceProfiles ADD COLUMN MinMemoryMb INTEGER NOT NULL DEFAULT 512;
                    ALTER TABLE InstanceProfiles ADD COLUMN MaxMemoryMb INTEGER NOT NULL DEFAULT 4096;
                    PRAGMA user_version = 6;
                    COMMIT;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 6;
            }

            if (schemaVersion == 6)
            {
                command.CommandText = """
                    BEGIN IMMEDIATE;
                    ALTER TABLE InstanceProfiles ADD COLUMN LoaderType TEXT NOT NULL DEFAULT 'Vanilla';
                    ALTER TABLE InstanceProfiles ADD COLUMN LoaderVersion TEXT NULL;
                    PRAGMA user_version = 7;
                    COMMIT;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 7;
            }

            if (schemaVersion == 7)
            {
                command.CommandText = """
                    BEGIN IMMEDIATE;
                    ALTER TABLE InstanceProfiles ADD COLUMN ModrinthPackPath TEXT NULL;
                    ALTER TABLE InstanceProfiles ADD COLUMN ModrinthSelectedOptionalFilesJson TEXT NOT NULL DEFAULT '[]';
                    PRAGMA user_version = 8;
                    COMMIT;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 8;
            }

            if (schemaVersion == 8)
            {
                command.CommandText = """
                    BEGIN IMMEDIATE;
                    ALTER TABLE InstanceProfiles ADD COLUMN UseAutomaticMemory INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE InstanceProfiles ADD COLUMN JavaExecutablePath TEXT NULL;
                    ALTER TABLE InstanceProfiles ADD COLUMN CustomJavaArguments TEXT NOT NULL DEFAULT '';
                    ALTER TABLE InstanceProfiles ADD COLUMN CustomEnvironmentVariablesJson TEXT NOT NULL DEFAULT '{}';
                    ALTER TABLE InstanceProfiles ADD COLUMN Fullscreen INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE InstanceProfiles ADD COLUMN WindowWidth INTEGER NOT NULL DEFAULT 854;
                    ALTER TABLE InstanceProfiles ADD COLUMN WindowHeight INTEGER NOT NULL DEFAULT 480;
                    PRAGMA user_version = 9;
                    COMMIT;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 9;
            }

            if (schemaVersion == 9)
            {
                command.CommandText = """
                    BEGIN IMMEDIATE;
                    ALTER TABLE InstanceProfiles ADD COLUMN UseGlobalLaunchSettings INTEGER NOT NULL DEFAULT 0;
                    DELETE FROM AppSettings WHERE Key = 'MicrosoftClientId';
                    PRAGMA user_version = 10;
                    COMMIT;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 10;
            }

            if (schemaVersion == 10)
            {
                command.CommandText = """
                    BEGIN IMMEDIATE;
                    ALTER TABLE InstanceProfiles ADD COLUMN CurseForgePackPath TEXT NULL;
                    PRAGMA user_version = 11;
                    COMMIT;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 11;
            }

            if (schemaVersion == 11)
            {
                command.CommandText = """
                    BEGIN IMMEDIATE;
                    ALTER TABLE InstanceProfiles ADD COLUMN ModrinthProjectId TEXT NULL;
                    ALTER TABLE InstanceProfiles ADD COLUMN ModrinthPackVersionId TEXT NULL;
                    ALTER TABLE InstanceProfiles ADD COLUMN ModrinthPackVersionNumber TEXT NULL;
                    PRAGMA user_version = 12;
                    COMMIT;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 12;
            }

            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<LauncherSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM AppSettings WHERE Key IN ('Theme', 'GlobalLaunchSettings');";

        string? themeValue = null;
        GlobalLaunchSettings? globalLaunchSettings = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(0) == "Theme")
                {
                    themeValue = reader.GetString(1);
                }
                else if (reader.GetString(0) == "GlobalLaunchSettings")
                {
                    try
                    {
                        globalLaunchSettings = JsonSerializer.Deserialize<GlobalLaunchSettings>(reader.GetString(1));
                    }
                    catch (JsonException)
                    {
                        globalLaunchSettings = null;
                    }
                }
            }
        }

        var theme = Enum.TryParse<ThemePreference>(themeValue, ignoreCase: true, out var parsedTheme)
            ? parsedTheme
            : LauncherSettings.Default.Theme;
        return new LauncherSettings(theme, globalLaunchSettings ?? GlobalLaunchSettings.Default);
    }

    public async Task SaveSettingsAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AppSettings (Key, Value) VALUES ('Theme', $theme)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            INSERT INTO AppSettings (Key, Value) VALUES ('GlobalLaunchSettings', $launchSettings)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("$theme", settings.Theme.ToString());
        command.Parameters.AddWithValue("$launchSettings", JsonSerializer.Serialize(settings.GlobalLaunchSettings));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveGlobalLaunchSettingsAsync(GlobalLaunchSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = await GetSettingsAsync(cancellationToken);
        await SaveSettingsAsync(current with { GlobalLaunchSettings = settings }, cancellationToken);
    }

    public async Task<IReadOnlyList<InstanceProfile>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, VersionId, State, CreatedAtUtc, LastError, MinMemoryMb, MaxMemoryMb, LoaderType, LoaderVersion, ModrinthPackPath, ModrinthSelectedOptionalFilesJson,
                   UseAutomaticMemory, JavaExecutablePath, CustomJavaArguments, CustomEnvironmentVariablesJson, Fullscreen, WindowWidth, WindowHeight, UseGlobalLaunchSettings,
                   ModrinthProjectId, ModrinthPackVersionId, ModrinthPackVersionNumber
            FROM InstanceProfiles
            ORDER BY CreatedAtUtc DESC;
            """;

        var profiles = new List<InstanceProfile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            profiles.Add(ReadProfile(reader));
        }

        return profiles;
    }

    public async Task<InstanceProfile?> GetProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, VersionId, State, CreatedAtUtc, LastError, MinMemoryMb, MaxMemoryMb, LoaderType, LoaderVersion, ModrinthPackPath, ModrinthSelectedOptionalFilesJson,
                   UseAutomaticMemory, JavaExecutablePath, CustomJavaArguments, CustomEnvironmentVariablesJson, Fullscreen, WindowWidth, WindowHeight, UseGlobalLaunchSettings,
                   ModrinthProjectId, ModrinthPackVersionId, ModrinthPackVersionNumber
            FROM InstanceProfiles
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProfile(reader) : null;
    }

    public async Task SetProfileStateAsync(
        Guid profileId,
        InstanceProfileState state,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE InstanceProfiles
            SET State = $state, LastError = $error
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetInstanceLaunchSettingsAsync(
        Guid profileId,
        InstanceLaunchSettings settings,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE InstanceProfiles
            SET MinMemoryMb = $minMemoryMb, MaxMemoryMb = $maxMemoryMb,
                UseAutomaticMemory = $useAutomaticMemory, JavaExecutablePath = $javaExecutablePath,
                CustomJavaArguments = $customJavaArguments, CustomEnvironmentVariablesJson = $customEnvironmentVariablesJson,
                Fullscreen = $fullscreen, WindowWidth = $windowWidth, WindowHeight = $windowHeight,
                UseGlobalLaunchSettings = $useGlobalSettings
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$minMemoryMb", settings.MinMemoryMb);
        command.Parameters.AddWithValue("$maxMemoryMb", settings.MaxMemoryMb);
        command.Parameters.AddWithValue("$useAutomaticMemory", settings.UseAutomaticMemory ? 1 : 0);
        command.Parameters.AddWithValue("$javaExecutablePath", (object?)settings.JavaExecutablePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$customJavaArguments", settings.CustomJavaArguments);
        command.Parameters.AddWithValue("$customEnvironmentVariablesJson", JsonSerializer.Serialize(settings.CustomEnvironmentVariables));
        command.Parameters.AddWithValue("$fullscreen", settings.Fullscreen ? 1 : 0);
        command.Parameters.AddWithValue("$windowWidth", settings.WindowWidth);
        command.Parameters.AddWithValue("$windowHeight", settings.WindowHeight);
        command.Parameters.AddWithValue("$useGlobalSettings", settings.UseGlobalSettings ? 1 : 0);
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new KeyNotFoundException("The selected instance profile no longer exists.");
        }
    }

    public async Task RecoverInterruptedInstallationsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE InstanceProfiles
            SET State = 'Failed', LastError = 'Installation was interrupted when Sakana closed.'
            WHERE State = 'Installing';
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryCreateProfileAsync(InstanceProfile profile, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO InstanceProfiles (Id, Name, VersionId, State, CreatedAtUtc, MinMemoryMb, MaxMemoryMb, LoaderType, LoaderVersion, ModrinthPackPath, ModrinthSelectedOptionalFilesJson, UseGlobalLaunchSettings, ModrinthProjectId, ModrinthPackVersionId, ModrinthPackVersionNumber)
            VALUES ($id, $name, $version, $state, $created, $minMemoryMb, $maxMemoryMb, $loaderType, $loaderVersion, $modrinthPackPath, $modrinthSelectedOptionalFilesJson, $useGlobalSettings, $modrinthProjectId, $modrinthPackVersionId, $modrinthPackVersionNumber)
            ON CONFLICT(Name) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$version", profile.VersionId);
        command.Parameters.AddWithValue("$state", profile.State.ToString());
        command.Parameters.AddWithValue("$created", profile.CreatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$minMemoryMb", profile.MinMemoryMb);
        command.Parameters.AddWithValue("$maxMemoryMb", profile.MaxMemoryMb);
        command.Parameters.AddWithValue("$loaderType", profile.LoaderType.ToString());
        command.Parameters.AddWithValue("$loaderVersion", (object?)profile.LoaderVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$modrinthPackPath", (object?)profile.ModrinthPackPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$modrinthSelectedOptionalFilesJson", profile.ModrinthSelectedOptionalFilesJson);
        command.Parameters.AddWithValue("$useGlobalSettings", profile.UseGlobalLaunchSettings ? 1 : 0);
        command.Parameters.AddWithValue("$modrinthProjectId", (object?)profile.ModrinthProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$modrinthPackVersionId", (object?)profile.ModrinthPackVersionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$modrinthPackVersionNumber", (object?)profile.ModrinthPackVersionNumber ?? DBNull.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task UpdateModrinthPackSourceAsync(
        Guid profileId,
        string packPath,
        string versionId,
        string versionNumber,
        string selectedOptionalFilesJson,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE InstanceProfiles
            SET ModrinthPackPath = $packPath,
                ModrinthPackVersionId = $versionId,
                ModrinthPackVersionNumber = $versionNumber,
                ModrinthSelectedOptionalFilesJson = $selectedOptionalFilesJson
            WHERE Id = $id AND ModrinthProjectId IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$packPath", packPath);
        command.Parameters.AddWithValue("$versionId", versionId);
        command.Parameters.AddWithValue("$versionNumber", versionNumber);
        command.Parameters.AddWithValue("$selectedOptionalFilesJson", selectedOptionalFilesJson);
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new KeyNotFoundException("The selected Modrinth-linked instance profile no longer exists.");
        }
    }

    public async Task<bool> TryRenameProfileAsync(Guid profileId, string name, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE OR IGNORE InstanceProfiles
            SET Name = $name
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using (var historyCommand = connection.CreateCommand())
        {
            historyCommand.Transaction = transaction;
            historyCommand.CommandText = "DELETE FROM LaunchHistory WHERE InstanceId = $id;";
            historyCommand.Parameters.AddWithValue("$id", profileId.ToString("D"));
            await historyCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var profileCommand = connection.CreateCommand())
        {
            profileCommand.Transaction = transaction;
            profileCommand.CommandText = "DELETE FROM InstanceProfiles WHERE Id = $id;";
            profileCommand.Parameters.AddWithValue("$id", profileId.ToString("D"));
            if (await profileCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new KeyNotFoundException("The selected instance profile no longer exists.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AddLaunchHistoryAsync(
        MinecraftLaunchHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO LaunchHistory (Id, InstanceId, VersionId, StartedAtUtc, EndedAtUtc, ExitCode)
            VALUES ($id, $instanceId, $versionId, $startedAt, $endedAt, $exitCode);
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue("$instanceId", entry.InstanceId.ToString("D"));
        command.Parameters.AddWithValue("$versionId", entry.VersionId);
        command.Parameters.AddWithValue("$startedAt", FormatUtc(entry.StartedAtUtc));
        var endedAt = (object?)entry.EndedAtUtc?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? DBNull.Value;
        command.Parameters.AddWithValue("$endedAt", endedAt);
        command.Parameters.AddWithValue("$exitCode", (object?)entry.ExitCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteLaunchHistoryAsync(
        Guid launchId,
        DateTimeOffset endedAtUtc,
        int? exitCode,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LaunchHistory
            SET EndedAtUtc = $endedAt, ExitCode = $exitCode
            WHERE Id = $id AND EndedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$endedAt", FormatUtc(endedAtUtc));
        command.Parameters.AddWithValue("$exitCode", (object?)exitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", launchId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MinecraftLaunchHistoryEntry>> ListLaunchHistoryAsync(
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Launch history page size must be between 1 and 200.");
        }

        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, InstanceId, VersionId, StartedAtUtc, EndedAtUtc, ExitCode
            FROM LaunchHistory
            ORDER BY StartedAtUtc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var entries = new List<MinecraftLaunchHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new MinecraftLaunchHistoryEntry(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetInt32(5)));
        }

        return entries;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken);
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static InstanceProfile ReadProfile(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        Enum.Parse<InstanceProfileState>(reader.GetString(3)),
        DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetInt32(6),
        reader.GetInt32(7),
        Enum.Parse<MinecraftLoaderType>(reader.GetString(8)),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.GetString(11),
        reader.GetInt32(12) != 0,
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.GetString(14),
        reader.GetString(15),
        reader.GetInt32(16) != 0,
        reader.GetInt32(17),
        reader.GetInt32(18),
        reader.GetInt32(19) != 0,
        reader.IsDBNull(20) ? null : reader.GetString(20),
        reader.IsDBNull(21) ? null : reader.GetString(21),
        reader.IsDBNull(22) ? null : reader.GetString(22));

    private static string FormatUtc(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}

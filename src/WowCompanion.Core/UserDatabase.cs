using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>
/// Your own data (checklists, recipe tracking later). Separate from the game data so rebuilds never touch it.
/// Add a new entry to Migrations for every schema change; never edit an old one.
/// </summary>
public static class UserDatabase
{
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE checklist_items (
            id INTEGER PRIMARY KEY,
            title TEXT NOT NULL,
            reset_rule TEXT NOT NULL DEFAULT 'manual',
            done INTEGER NOT NULL DEFAULT 0,
            last_done_utc TEXT
        );
        """,
        """
        CREATE TABLE craft_list (
            spell_id INTEGER PRIMARY KEY,
            quantity INTEGER NOT NULL CHECK (quantity > 0),
            added_utc TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """,
        """
        CREATE TABLE talent_builds (
            class_id INTEGER PRIMARY KEY,
            level INTEGER NOT NULL,
            ranks TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE gear_sets (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            class_id INTEGER NOT NULL,
            race_id INTEGER NOT NULL,
            level INTEGER NOT NULL,
            base_overrides TEXT NOT NULL DEFAULT '',
            created_utc TEXT NOT NULL
        );
        CREATE TABLE gear_set_items (
            set_id INTEGER NOT NULL,
            slot INTEGER NOT NULL,
            item_id INTEGER NOT NULL,
            PRIMARY KEY (set_id, slot)
        );
        CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """,
        """
        CREATE TABLE talent_sets (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            class_id INTEGER NOT NULL,
            name TEXT NOT NULL,
            level INTEGER NOT NULL,
            ranks TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );
        INSERT INTO talent_sets (class_id, name, level, ranks, updated_utc)
            SELECT class_id, 'Main', level, ranks, updated_utc FROM talent_builds;
        CREATE TABLE calc_snapshots (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            data TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE notes (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            title TEXT NOT NULL,
            body TEXT NOT NULL DEFAULT '',
            remind_utc TEXT,
            repeat TEXT NOT NULL DEFAULT 'none',
            done INTEGER NOT NULL DEFAULT 0,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE learned_items (
            id INTEGER PRIMARY KEY,
            data TEXT NOT NULL,
            learned_utc TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE learned_quests (
            id INTEGER PRIMARY KEY,
            data TEXT NOT NULL,
            learned_utc TEXT NOT NULL
        );
        """,
        """
        ALTER TABLE craft_list ADD COLUMN done INTEGER NOT NULL DEFAULT 0;
        """,
        """
        CREATE TABLE seen_data (
            key TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            data TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE base_stats (
            class_id INTEGER NOT NULL,
            race_id INTEGER NOT NULL,
            level INTEGER NOT NULL,
            str REAL NOT NULL, agi REAL NOT NULL, sta REAL NOT NULL, int REAL NOT NULL, spi REAL NOT NULL,
            health INTEGER NOT NULL DEFAULT 0,
            mana INTEGER NOT NULL DEFAULT 0,
            naked INTEGER NOT NULL DEFAULT 0,
            build TEXT NOT NULL DEFAULT '',
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (class_id, race_id, level)
        );
        """,
        """
        CREATE TABLE trainer_skills (
            profession TEXT NOT NULL,
            name TEXT NOT NULL,
            skill INTEGER NOT NULL,
            item_id INTEGER NOT NULL DEFAULT 0,
            level INTEGER NOT NULL DEFAULT 0,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (profession, name)
        );
        """,
        // Addon 0.10.0 and older recorded base stats while logging out, when every stat reads 0 (and as "naked",
        // so they were never replaced).
        """
        DELETE FROM base_stats WHERE str <= 0 OR agi <= 0 OR sta <= 0 OR int <= 0 OR spi <= 0;
        """,
        """
        CREATE TABLE seen_places (
            zone TEXT NOT NULL,
            name TEXT NOT NULL,
            x REAL NOT NULL,
            y REAL NOT NULL,
            samples INTEGER NOT NULL,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (zone, name)
        );
        """,
    ];

    public static void Initialize(string path)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();

        int current;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version";
            current = Convert.ToInt32(command.ExecuteScalar());
        }

        for (var version = current; version < Migrations.Length; version++)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, Migrations[version]);
            Execute(connection, transaction, $"PRAGMA user_version = {version + 1}");
            transaction.Commit();
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

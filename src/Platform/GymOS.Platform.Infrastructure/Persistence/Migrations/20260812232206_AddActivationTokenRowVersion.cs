using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Deliberately empty: <c>user_activation_tokens</c> gains an optimistic-concurrency token and no
    /// column.
    ///
    /// <para><c>xmin</c> is a PostgreSQL <b>system column</b> that already exists on every table, so
    /// there is nothing to add. The scaffolded version of this migration called
    /// <c>AddColumn&lt;uint&gt;("xmin", ...)</c>, which the server rejects outright — "column name
    /// xmin conflicts with a system column name". Compare the tables that carried a row version from
    /// the start: their <c>CreateTable</c> lists xmin, and Npgsql omits it from the emitted DDL.</para>
    ///
    /// <para>It is kept rather than deleted because EF compares the model against the migration
    /// history and refuses to run with <c>PendingModelChangesWarning</c> otherwise. The change this
    /// records is real and lives entirely in the model snapshot.</para>
    /// </summary>
    public partial class AddActivationTokenRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No DDL: see the type's summary.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No DDL: see the type's summary.
        }
    }
}

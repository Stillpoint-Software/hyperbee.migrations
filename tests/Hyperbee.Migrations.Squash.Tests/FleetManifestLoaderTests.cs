using FluentAssertions;
using Hyperbee.Migrations.Cli.FleetManifest;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 7 Task 7.2 — fleet manifest YAML parser + validation.

[TestClass]
public class FleetManifestLoaderTests
{
    // Each test uses a unique env var name so MSTest's parallel execution
    // doesn't race finally-cleanup of one test against another's setup.

    [TestMethod]
    public void Loader_HappyPath_ParsesEnvironmentsAndOverrides()
    {
        WithEnv( ("TEST_FM_HAPPY_PROD", "Host=prod-db;Database=app;Username=u;Password=p"),
                 ("TEST_FM_HAPPY_STG", "Host=stg-db;Database=app;Username=u;Password=p") )
            .Run( () =>
            {
                var yaml = """
                    fleet:
                      - name: prod
                        connection: ${TEST_FM_HAPPY_PROD}
                        topology:
                          server-major: "16"
                      - name: staging
                        connection: ${TEST_FM_HAPPY_STG}

                    squash-overrides:
                      accept-stranding:
                        - environment: staging
                          ticket-id: FLEET-1234
                          owner: ops@example.com
                          reason: "Staging is intentionally stranded for the v3 rebuild this quarter."
                    """;

                var model = FleetManifestLoader.LoadFromString( yaml );

                model.Fleet.Should().HaveCount( 2 );
                model.Fleet[0].Name.Should().Be( "prod" );
                model.Fleet[0].Connection.Should().Contain( "Host=prod-db" );
                model.Fleet[0].Topology.Should().ContainKey( "server-major" );

                model.SquashOverrides!.AcceptStranding.Should().HaveCount( 1 );
                var overridden = model.SquashOverrides.AcceptStranding[0];
                overridden.Environment.Should().Be( "staging" );
                overridden.TicketId.Should().Be( "FLEET-1234" );
                overridden.Reason.Length.Should().BeGreaterThanOrEqualTo( 20 );
            } );
    }

    [TestMethod]
    public void Loader_UnsetEnvironmentVariable_RaisesClearError()
    {
        var yaml = """
            fleet:
              - name: prod
                connection: ${UNSET_VARIABLE_TEST_8675309}
            """;

        var act = () => FleetManifestLoader.LoadFromString( yaml );
        act.Should().Throw<MigrationException>()
            .WithMessage( "*UNSET_VARIABLE_TEST_8675309*" );
    }

    [TestMethod]
    public void Loader_DuplicateEnvironmentName_RaisesError()
    {
        WithEnv( ("TEST_FM_DUP", "x") ).Run( () =>
        {
            var yaml = """
                fleet:
                  - name: prod
                    connection: ${TEST_FM_DUP}
                  - name: prod
                    connection: ${TEST_FM_DUP}
                """;

            var act = () => FleetManifestLoader.LoadFromString( yaml );
            act.Should().Throw<MigrationException>().WithMessage( "*duplicate*prod*" );
        } );
    }

    [TestMethod]
    public void Loader_StrandingEntryReferencesUnknownEnvironment_RaisesError()
    {
        WithEnv( ("TEST_FM_UNKENV", "x") ).Run( () =>
        {
            var yaml = """
                fleet:
                  - name: prod
                    connection: ${TEST_FM_UNKENV}

                squash-overrides:
                  accept-stranding:
                    - environment: nonexistent
                      ticket-id: FLEET-1234
                      owner: ops
                      reason: "This environment doesn't exist in the fleet list."
                """;

            var act = () => FleetManifestLoader.LoadFromString( yaml );
            act.Should().Throw<MigrationException>()
                .WithMessage( "*unknown environment*nonexistent*" );
        } );
    }

    [TestMethod]
    public void Loader_StrandingTicketIdInvalid_RaisesError()
    {
        WithEnv( ("TEST_FM_TICKID", "x") ).Run( () =>
        {
            var yaml = """
                fleet:
                  - name: prod
                    connection: ${TEST_FM_TICKID}

                squash-overrides:
                  accept-stranding:
                    - environment: prod
                      ticket-id: "bad ticket id with spaces"
                      owner: ops
                      reason: "This ticket id has spaces and exceeds simple alphanumeric."
                """;

            var act = () => FleetManifestLoader.LoadFromString( yaml );
            act.Should().Throw<MigrationException>().WithMessage( "*ticket-id*invalid*" );
        } );
    }

    [TestMethod]
    public void Loader_StrandingReasonTooShort_RaisesError()
    {
        WithEnv( ("TEST_FM_SHORTRSN", "x") ).Run( () =>
        {
            var yaml = """
                fleet:
                  - name: prod
                    connection: ${TEST_FM_SHORTRSN}

                squash-overrides:
                  accept-stranding:
                    - environment: prod
                      ticket-id: FLEET-1
                      owner: ops
                      reason: "too short"
                """;

            var act = () => FleetManifestLoader.LoadFromString( yaml );
            act.Should().Throw<MigrationException>().WithMessage( "*reason*20 characters*" );
        } );
    }

    [TestMethod]
    public void Loader_StrandingExpiryBeyondMax_RaisesError()
    {
        WithEnv( ("TEST_FM_LONGEXP", "x") ).Run( () =>
        {
            // 200 days from now exceeds the 90-day cap per A15.
            var yaml =
                "fleet:\n" +
                "  - name: prod\n" +
                "    connection: ${TEST_FM_LONGEXP}\n" +
                "\n" +
                "squash-overrides:\n" +
                "  accept-stranding:\n" +
                "    - environment: prod\n" +
                "      ticket-id: FLEET-1\n" +
                "      owner: ops\n" +
                "      reason: \"Reason that is at least twenty characters long here.\"\n" +
                $"      expires: {DateTimeOffset.UtcNow.AddDays( 200 ):yyyy-MM-dd}\n";

            var act = () => FleetManifestLoader.LoadFromString( yaml );
            act.Should().Throw<MigrationException>().WithMessage( "*max-expiry-window*90*" );
        } );
    }

    [TestMethod]
    public void Loader_DefaultExpiry_Is30Days()
    {
        WithEnv( ("TEST_FM_DEFEXP", "x") ).Run( () =>
        {
            var yaml = """
                fleet:
                  - name: prod
                    connection: ${TEST_FM_DEFEXP}

                squash-overrides:
                  accept-stranding:
                    - environment: prod
                      ticket-id: FLEET-1
                      owner: ops
                      reason: "Reason that is at least twenty characters long here."
                """;

            var beforeLoad = DateTimeOffset.UtcNow;
            var model = FleetManifestLoader.LoadFromString( yaml );
            var afterLoad = DateTimeOffset.UtcNow;

            var entry = model.SquashOverrides!.AcceptStranding[0];
            var expires = DateTimeOffset.Parse( entry.Expires!, System.Globalization.CultureInfo.InvariantCulture );
            expires.Should().BeOnOrAfter( beforeLoad.AddDays( 30 ).AddSeconds( -2 ) );
            expires.Should().BeOnOrBefore( afterLoad.AddDays( 30 ).AddSeconds( 2 ) );
        } );
    }

    [TestMethod]
    public void BuildOverrideEntries_ProjectsToSquashOverrideEntry()
    {
        WithEnv( ("TEST_FM_BUILDPROJ", "x") ).Run( () =>
        {
            var yaml = """
                fleet:
                  - name: prod
                    connection: ${TEST_FM_BUILDPROJ}

                squash-overrides:
                  accept-stranding:
                    - environment: prod
                      ticket-id: FLEET-1
                      owner: ops@example.com
                      reason: "Reason that is at least twenty characters long here."
                """;

            var model = FleetManifestLoader.LoadFromString( yaml );
            var now = DateTimeOffset.UtcNow;
            var entries = FleetManifestLoader.BuildOverrideEntries( model, now );

            entries.Should().HaveCount( 1 );
            entries[0].EnvironmentName.Should().Be( "prod" );
            entries[0].TicketId.Should().Be( "FLEET-1" );
            entries[0].Owner.Should().Be( "ops@example.com" );
            entries[0].IsExpired( now ).Should().BeFalse();
            entries[0].IsExpired( now.AddDays( 31 ) ).Should().BeTrue();
        } );
    }

    // ---------------- helper ----------------

    private static EnvScope WithEnv( params (string Name, string Value)[] vars ) => new( vars );

    private sealed class EnvScope
    {
        private readonly (string Name, string Value)[] _vars;
        public EnvScope( (string Name, string Value)[] vars ) { _vars = vars; }

        public void Run( Action body )
        {
            foreach ( var (n, v) in _vars )
                Environment.SetEnvironmentVariable( n, v );
            try { body(); }
            finally
            {
                foreach ( var (n, _) in _vars )
                    Environment.SetEnvironmentVariable( n, null );
            }
        }
    }
}

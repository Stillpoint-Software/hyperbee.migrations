using Microsoft.Extensions.Configuration;

// Enhancement to microsoft's CommandLineConfigurationProvider with array support

namespace Hyperbee.MigrationRunner.Aerospike;

public static class CommandLineConfigurationExtensions
{
    public static IConfigurationBuilder AddCommandLineEx( this IConfigurationBuilder configurationBuilder, string[] args )
    {
        return configurationBuilder.AddCommandLineEx( args, switchMappings: null );
    }

    public static IConfigurationBuilder AddCommandLineEx( this IConfigurationBuilder configurationBuilder, string[] args, IDictionary<string, string> switchMappings )
    {
        configurationBuilder.Add( new CommandLineConfigurationSource { Args = args, SwitchMappings = switchMappings } );
        return configurationBuilder;
    }

    public static IConfigurationBuilder AddCommandLine( this IConfigurationBuilder builder, Action<CommandLineConfigurationSource> configureSource )
        => builder.Add( configureSource );
}

public class CommandLineConfigurationSource : IConfigurationSource
{
    public IDictionary<string, string> SwitchMappings { get; set; }

    public IEnumerable<string> Args { get; set; }

    public IConfigurationProvider Build( IConfigurationBuilder builder )
    {
        return new CommandLineConfigurationProvider( Args, SwitchMappings );
    }
}

public class CommandLineConfigurationProvider : ConfigurationProvider
{
    private readonly Dictionary<string, string> _switchMappings;

    public CommandLineConfigurationProvider( IEnumerable<string> args, IDictionary<string, string> switchMappings = null )
    {
        Args = args ?? throw new ArgumentNullException( nameof( args ) );

        if ( switchMappings != null )
        {
            _switchMappings = GetValidatedSwitchMappingsCopy( switchMappings );
        }
    }

    protected IEnumerable<string> Args { get; }

    public override void Load()
    {
        static bool IsArrayKey( string key ) => key.StartsWith( '[' ) && key.EndsWith( ']' );

        var data = new Dictionary<string, IList<string>>( StringComparer.OrdinalIgnoreCase );

        using ( var enumerator = Args.GetEnumerator() )
        {
            while ( enumerator.MoveNext() )
            {
                var currentArg = enumerator.Current;
                var keyStartIndex = 0;

                if ( currentArg!.StartsWith( "--" ) )
                {
                    keyStartIndex = 2;
                }
                else if ( currentArg.StartsWith( "-" ) )
                {
                    keyStartIndex = 1;
                }
                else if ( currentArg.StartsWith( "/" ) )
                {
                    currentArg = $"--{currentArg[1..]}";
                    keyStartIndex = 2;
                }

                var separator = currentArg.IndexOf( '=' );

                string key;
                string value;
                if ( separator < 0 )
                {
                    if ( keyStartIndex == 0 )
                    {
                        continue;
                    }

                    if ( _switchMappings != null && _switchMappings.TryGetValue( currentArg, out var mappedKey ) )
                    {
                        key = mappedKey;
                    }
                    else if ( keyStartIndex == 1 )
                    {
                        continue;
                    }
                    else
                    {
                        key = currentArg[keyStartIndex..];
                    }

                    var previousKey = enumerator.Current;
                    if ( !enumerator.MoveNext() )
                    {
                        continue;
                    }

                    value = enumerator.Current;
                }
                else
                {
                    var keySegment = currentArg[..separator];

                    if ( _switchMappings != null && _switchMappings.TryGetValue( keySegment, out var mappedKeySegment ) )
                    {
                        key = mappedKeySegment;
                    }
                    else if ( keyStartIndex == 1 )
                    {
                        throw new FormatException( $"Short switch `{currentArg}` is not defined." );
                    }
                    else
                    {
                        key = currentArg[keyStartIndex..separator];
                    }

                    value = currentArg[(separator + 1)..];
                }

                if ( IsArrayKey( key ) )
                {
                    if ( !data.TryGetValue( key, out var values ) )
                    {
                        values = new List<string>();
                        data[key] = values;
                    }

                    values.Add( value );
                }
                else
                {
                    data[key] = new List<string> { value };
                }
            }
        }

        var final = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

        foreach ( var (key, values) in data )
        {
            if ( !IsArrayKey( key ) )
            {
                final[key] = values[0];
                continue;
            }

            var index = 0;
            var name = key.Trim( '[', ']', ' ', '\t' );
            foreach ( var value in values )
            {
                final[$"{name}:{index++}"] = value;
            }
        }

        Data = final;
    }

    private static Dictionary<string, string> GetValidatedSwitchMappingsCopy( IDictionary<string, string> switchMappings )
    {
        var switchMappingsCopy = new Dictionary<string, string>( switchMappings.Count, StringComparer.OrdinalIgnoreCase );
        foreach ( var mapping in switchMappings )
        {
            if ( !mapping.Key.StartsWith( "-" ) && !mapping.Key.StartsWith( "--" ) )
            {
                throw new ArgumentException( $"Invalid switch mapping `{mapping.Key}`.", nameof( switchMappings ) );
            }

            if ( switchMappingsCopy.ContainsKey( mapping.Key ) )
            {
                throw new ArgumentException( $"Invalid switch mapping `{mapping.Key}`.", nameof( switchMappings ) );
            }

            switchMappingsCopy.Add( mapping.Key, mapping.Value );
        }

        return switchMappingsCopy;
    }
}

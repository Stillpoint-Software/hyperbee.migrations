using Microsoft.Extensions.Configuration;

// Enhancement to microsoft's CommandLineConfigurationProvider with array support

namespace Hyperbee.MigrationRunner.Postgres;

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

        //var data = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
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
                    // "/SomeSwitch" is equivalent to "--SomeSwitch" when interpreting switch mappings
                    // So we do a conversion to simplify later processing
                    currentArg = $"--{currentArg[1..]}";
                    keyStartIndex = 2;
                }

                var separator = currentArg.IndexOf( '=' );

                string key;
                string value;
                if ( separator < 0 )
                {
                    // If there is neither equal sign nor prefix in current argument, it is an invalid format
                    if ( keyStartIndex == 0 )
                    {
                        // Ignore invalid formats
                        continue;
                    }

                    // If the switch is a key in given switch mappings, interpret it
                    if ( _switchMappings != null && _switchMappings.TryGetValue( currentArg, out var mappedKey ) )
                    {
                        key = mappedKey;
                    }
                    // If the switch starts with a single "-" and it isn't in given mappings , it is an invalid usage so ignore it
                    else if ( keyStartIndex == 1 )
                    {
                        continue;
                    }
                    // Otherwise, use the switch name directly as a key
                    else
                    {
                        key = currentArg[keyStartIndex..];
                    }

                    var previousKey = enumerator.Current;
                    if ( !enumerator.MoveNext() )
                    {
                        // ignore missing values
                        continue;
                    }

                    value = enumerator.Current;
                }
                else
                {
                    var keySegment = currentArg[..separator];

                    // If the switch is a key in given switch mappings, interpret it
                    if ( _switchMappings != null && _switchMappings.TryGetValue( keySegment, out var mappedKeySegment ) )
                    {
                        key = mappedKeySegment;
                    }
                    // If the switch starts with a single "-" and it isn't in given mappings , it is an invalid usage
                    else if ( keyStartIndex == 1 )
                    {
                        throw new FormatException( $"Short switch `{currentArg}` is not defined." );
                    }
                    // Otherwise, use the switch name directly as a key
                    else
                    {
                        key = currentArg[keyStartIndex..separator];
                    }

                    value = currentArg[(separator + 1)..];
                }

                /* BF MODIFIED

                // Override value when key is duplicated. So we always have the last argument win.
                data[key] = value;

                */

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
                    // If not an array then override value when key is duplicated.
                    // So we always have the last argument win.
                    data[key] = new List<string> { value };
                }

                // END MODIFIED
            }
        }

        /* BF MODIFIED
         
        Data = data;

        */

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

        // END MODIFIED
    }

    private static Dictionary<string, string> GetValidatedSwitchMappingsCopy( IDictionary<string, string> switchMappings )
    {
        // The dictionary passed in might be constructed with a case-sensitive comparer
        // However, the keys in configuration providers are all case-insensitive
        // So we check whether the given switch mappings contain duplicated keys with case-insensitive comparer
        var switchMappingsCopy = new Dictionary<string, string>( switchMappings.Count, StringComparer.OrdinalIgnoreCase );
        foreach ( var mapping in switchMappings )
        {
            // Only keys start with "--" or "-" are acceptable
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

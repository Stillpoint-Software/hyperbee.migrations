using System;
using System.Runtime.Serialization;

namespace Hyperbee.Migrations
{
    [Serializable]
    public class MigrationException : Exception
    {
        public MigrationException()
            : base( "Migration exception" )
        {
        }

        public MigrationException( string message )
            : base( message )
        {
        }

        public MigrationException( string message, Exception innerException )
            : base( message, innerException )
        {
        }

        internal MigrationException( SerializationInfo info, StreamingContext context )
            : base( info, context )
        {
        }
    }
}
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace Hyperbee.Migrations.Couchbase.Services
{
    internal class BasicAuthenticationHeaderProvider : IAuthenticationHeaderProvider
    {
        private readonly string _base64EncodedAuthenticationString;

        public BasicAuthenticationHeaderProvider( string user, string credential )
        {
            if ( user == null )
                throw new ArgumentNullException( nameof(user) );

            if ( credential == null )
                throw new ArgumentNullException( nameof(credential) );

            var bytes = Encoding.ASCII.GetBytes( $"{user}:{credential}" );
            _base64EncodedAuthenticationString = Convert.ToBase64String( bytes );
        }

        public Task<AuthenticationHeaderValue> GetHeaderAsync( HttpRequestOptions options )
        {
            return Task.FromResult( new AuthenticationHeaderValue( "Basic", _base64EncodedAuthenticationString ) );
        }
    }
}
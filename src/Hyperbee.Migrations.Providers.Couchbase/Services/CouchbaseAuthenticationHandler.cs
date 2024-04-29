using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Couchbase;
using Microsoft.Extensions.Options;

namespace Hyperbee.Migrations.Providers.Couchbase.Services
{
    internal class CouchbaseAuthenticationHandler : DelegatingHandler
    {
        private readonly IAuthenticationHeaderProvider _authenticationHeaderProvider;

        public CouchbaseAuthenticationHandler( IOptions<ClusterOptions> options )
        {
            var userName = options.Value.UserName;
            var password = options.Value.Password;

            _authenticationHeaderProvider = new BasicAuthenticationHeaderProvider( userName, password );
        }

        protected override async Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
        {
            request.Headers.Authorization = await _authenticationHeaderProvider.GetHeaderAsync( request.Options ).ConfigureAwait( false );
            return await base.SendAsync( request, cancellationToken ).ConfigureAwait( false );
        }
    }
}

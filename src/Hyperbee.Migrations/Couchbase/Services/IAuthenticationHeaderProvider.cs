using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Hyperbee.Migrations.Couchbase.Services
{
    internal interface IAuthenticationHeaderProvider
    {
        Task<AuthenticationHeaderValue> GetHeaderAsync( HttpRequestOptions options );
    }
}
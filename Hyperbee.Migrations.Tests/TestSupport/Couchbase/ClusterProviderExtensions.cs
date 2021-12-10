using System.Collections.Generic;
using System.Threading.Tasks;
using Couchbase;
using Couchbase.Core.Exceptions.KeyValue;
using Couchbase.Extensions.DependencyInjection;
using Couchbase.KeyValue;
using Couchbase.Query;
using NSubstitute;

namespace Hyperbee.Migrations.Tests.TestSupport.Couchbase
{
    public static class ClusterProviderExtensions
    {
        public static IClusterProvider SetupData<TValue>( this IClusterProvider clusterProvider, IDictionary<string, TValue> data )
        {
            return clusterProvider.SetupData( data, null );
        }

        public static IClusterProvider SetupData<TValue>( this IClusterProvider clusterProvider, IDictionary<string, TValue> data, QueryProvider<TValue> queryProvider )
        {
            // Query

            var cluster = Substitute.For<ICluster>();
            queryProvider ??= new QueryProvider<TValue>();

            cluster.QueryAsync<TValue>( Arg.Any<string>(), Arg.Any<QueryOptions>() ).Returns( x =>
            {
                var queryString = x.ArgAt<string>( 0 );
                var queryOptions = x.ArgAt<QueryOptions>( 1 );

                var (rows, errors) = queryProvider.InvokeQuery( data, queryString, queryOptions );

                var queryResult = Substitute.For<IQueryResult<TValue>>();

                queryResult.Rows.Returns( rows );
                queryResult.Errors.Returns( errors );

                return queryResult;
            } );

            // Collection

            var collection = Substitute.For<ICouchbaseCollection>();
            
            collection.GetAsync( Arg.Any<string>() ).Returns( x =>
            {
                if ( !data.TryGetValue( x.Arg<string>(), out var document ) )
                    throw new DocumentNotFoundException();

                var getResult = Substitute.For<IGetResult>();
                getResult.ContentAs<TValue>().Returns( document );

                return getResult;
            } );

            collection.RemoveAsync( Arg.Any<string>() ).Returns( x =>
            {
                data.Remove( x.Arg<string>() );
                return Task.CompletedTask;
            } );

            collection.UpsertAsync( Arg.Any<string>(), Arg.Any<TValue>() ).Returns( x =>
            {
                data[x.Arg<string>()] = x.Arg<TValue>();
                return Substitute.For<IMutationResult>();
            } );

            // Bucket

            var bucket = Substitute.For<IBucket>();
            bucket.DefaultCollectionAsync().Returns( collection );

            cluster.BucketAsync( Arg.Any<string>() ).Returns( bucket );

            // Provider

            clusterProvider.GetClusterAsync().Returns( cluster );
            return clusterProvider;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Couchbase.Query;

namespace Hyperbee.Migrations.Tests.TestSupport.Couchbase
{
    public class QueryProvider<TValue>
    {
        public Action<QueryContext> OnQuery { get; set; }

        private static readonly Action<QueryContext> DefaultOnQuery = x => x.ReturnsAll(); 

        internal (IAsyncEnumerable<TValue> Rows, IList<Error> Errors) InvokeQuery(  IDictionary<string, TValue> data, string queryString, QueryOptions queryOptions )
        {
            var queryInfo = new QueryContext
            {
                Bucket = data,
                QueryString = queryString,
                QueryOptions = queryOptions
            };

            // call the query
            (OnQuery ?? DefaultOnQuery)( queryInfo );

            var rows = queryInfo.Result ?? Enumerable.Empty<TValue>();
            var errors = queryInfo.Error ?? Enumerable.Empty<Error>();

            return (rows.ToAsyncEnumerable(), errors.ToList());
        }

        public class QueryContext
        {
            public IDictionary<string, TValue> Bucket { get; init; }
            public string QueryString { get; init; }
            public QueryOptions QueryOptions { get; init; }

            public IEnumerable<TValue> Result { get; private set; }
            public IEnumerable<Error> Error { get; private set; }

            internal QueryContext()
            {
            }

            public IReadOnlyDictionary<string, object> Parameters()
            {
                return typeof(QueryOptions)
                    .GetField( "_parameters", BindingFlags.Instance | BindingFlags.NonPublic )
                    ?.GetValue( QueryOptions ) as IReadOnlyDictionary<string,object>;
            }

            public void Returns( Func<IDictionary<string, TValue>, IEnumerable<TValue>> selector ) => Result = selector( Bucket );
            public void Returns( IEnumerable<TValue> value ) => Result = value;
            public void ReturnsAll() => Result = Bucket?.Values;
            public void ReturnsEmpty() => Result = null;
            public void ReturnsError( IEnumerable<Error> errors ) => Error = errors;
            public void ReturnsError( Error error ) => Error = error == null ? null : new[] { error };
        }
    }
}
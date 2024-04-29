using Hyperbee.Migrations.Providers.Couchbase.Parsers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Tests
{
    [TestClass]
    public class KeyspaceParserTests
    {
        [DataTestMethod]
        [DataRow( "bucket" )]
        [DataRow( "`bucket`" )]
        public void Should_parse_1_dimensional_keyspace( string keyspace )
        {
            // arrange
            var parser = new KeyspaceParser();

            // act
            var result = parser.ParseExpression( keyspace, out _ );

            // assert
            Assert.IsNotNull( result );
            Assert.AreEqual( "bucket", result.BucketName );
            Assert.IsNull( result.Namespace );
            Assert.IsNull( result.ScopeName );
            Assert.IsNull( result.CollectionName );
        }

        [DataTestMethod]
        [DataRow( "bucket.collection" )]
        [DataRow( "bucket.`collection`" )]
        [DataRow( "`bucket`.collection" )]
        [DataRow( "`bucket`.`collection`" )]
        public void Should_parse_2_dimensional_keyspace( string keyspace )
        {
            // arrange
            var parser = new KeyspaceParser();

            // act
            var result = parser.ParseExpression( keyspace, out _ );

            // assert
            Assert.IsNotNull( result );
            Assert.AreEqual( "bucket", result.BucketName );
            Assert.AreEqual( "collection", result.CollectionName );
            Assert.IsNull( result.Namespace );
            Assert.IsNull( result.ScopeName );
        }

        [DataTestMethod]
        [DataRow( "bucket.scope.collection" )]
        [DataRow( "bucket.scope.`collection`" )]
        [DataRow( "bucket.`scope`.collection" )]
        [DataRow( "bucket.`scope`.`collection`" )]
        [DataRow( "`bucket`.scope.collection" )]
        [DataRow( "`bucket`.scope.`collection`" )]
        [DataRow( "`bucket`.`scope`.collection" )]
        [DataRow( "`bucket`.`scope`.`collection`" )]
        public void Should_parse_3_dimensional_keyspace( string keyspace )
        {
            // arrange
            var parser = new KeyspaceParser();

            // act
            var result = parser.ParseExpression( keyspace, out _ );

            // assert
            Assert.IsNotNull( result );
            Assert.AreEqual( "bucket", result.BucketName );
            Assert.AreEqual( "scope", result.ScopeName );
            Assert.AreEqual( "collection", result.CollectionName );
            Assert.IsNull( result.Namespace );
        }

        [DataTestMethod]
        [DataRow( "namespace:bucket.scope.collection" )]
        [DataRow( "`namespace`:`bucket`.`scope`.`collection`" )]
        public void Should_parse_4_dimensional_keyspace( string keyspace )
        {
            // arrange
            var parser = new KeyspaceParser();

            // act
            var result = parser.ParseExpression( keyspace, out _ );

            // assert
            Assert.IsNotNull( result );
            Assert.AreEqual( "bucket", result.BucketName );
            Assert.AreEqual( "scope", result.ScopeName );
            Assert.AreEqual( "collection", result.CollectionName );
            Assert.AreEqual( "namespace", result.Namespace );
        }
    }
}

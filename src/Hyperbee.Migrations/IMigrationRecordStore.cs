using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hyperbee.Migrations;

public interface IMigrationRecordStore
{
    Task InitializeAsync( CancellationToken cancellationToken = default );
    Task<IDisposable> CreateLockAsync();

    Task<bool> ExistsAsync( string recordId );
    Task DeleteAsync( string recordId );
    Task WriteAsync( string recordId );
}
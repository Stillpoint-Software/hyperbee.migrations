using System;
using System.Threading.Tasks;

namespace Hyperbee.Migrations;

public interface IMigrationRecordStore
{
    Task InitializeAsync();
    Task<IDisposable> CreateMutexAsync();

    Task<bool> ExistsAsync( string migrationId );
    Task DeleteAsync( string migrationId );
    Task StoreAsync( string migrationId );
}
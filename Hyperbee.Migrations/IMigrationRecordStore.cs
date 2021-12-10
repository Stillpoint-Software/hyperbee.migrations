using System;
using System.Threading.Tasks;

namespace Hyperbee.Migrations;

public interface IMigrationRecordStore
{
    Task InitializeAsync();
    Task<IMigrationRecord> LoadAsync( string migrationId );
    Task StoreAsync( string migrationId );
    Task DeleteAsync( IMigrationRecord record );
    Task<IDisposable> CreateMutexAsync();
}
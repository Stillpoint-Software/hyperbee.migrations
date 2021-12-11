using System;
using System.Threading.Tasks;

namespace Hyperbee.Migrations;

public interface IMigrationRecordStore
{
    Task InitializeAsync();
    Task<IDisposable> CreateLockAsync();

    Task<bool> ExistsAsync( string recordId );
    Task DeleteAsync( string recordId );
    Task StoreAsync( string recordId );
}
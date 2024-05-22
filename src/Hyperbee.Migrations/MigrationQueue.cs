using System.Collections.Concurrent;

namespace Hyperbee.Migrations;

public class MigrationItem
{
    public Migration Migration { get; set; }
    public Direction Direction { get; set; }
    public string RecordId { get; set; }
    public MigrationAttribute Attribute { get; set; }
    public CancellationToken CancellationToken { get; set; }
}

// Define a thread-safe queue for work items
public class MigrationQueue
{
    private readonly ConcurrentQueue<MigrationItem> _peekQueue;
    private readonly BlockingCollection<MigrationItem> _queue;

    public MigrationQueue()
    {

        _peekQueue = new ConcurrentQueue<MigrationItem>();
        _queue = new BlockingCollection<MigrationItem>( _peekQueue );
    }

    public void Add( MigrationItem item )
    {
        _queue.Add( item, CancellationToken.None );
        //       _queue.Add( item );
    }

    public MigrationItem TryPeek()
    {
        return _peekQueue.TryPeek( out MigrationItem item ) ? item : null;
    }

    public MigrationItem GetNextItem()
    {
        var test = _queue.ToArray();
        return test.FirstOrDefault();
    }

    //Have to remove each item in BlockCollection
    public void FinishedItem( MigrationItem item )
    {
        lock ( _peekQueue )
        {
            // Convert BlockingCollection to a list for easier removal
            var list = _queue.ToList();

            // Find and remove the item from the list
            list.Remove( item );

            // Clear the queue and add back items except the removed one
            while ( _queue.Count > 0 )
            {
                // Take an item from the collection
                _queue.Take();
            }

            //back new list into queue
            foreach ( var i in list )
                _queue.Add( i );
        }
    }

    public void CompleteAdding()
    {
        _queue.CompleteAdding();
    }
    public bool Finished()
    {
        return _queue.IsAddingCompleted && _queue.IsCompleted;
    }

    public int Count()
    {
        return _queue.Count;
    }
}

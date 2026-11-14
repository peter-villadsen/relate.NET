using Microsoft.Win32.SafeHandles;

namespace Relate.Net.Interop;

/// <summary>Owns a native <c>relate_database</c>; closes it on release.</summary>
internal sealed class DatabaseHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public DatabaseHandle() : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.relate_database_close(handle);
        return true;
    }
}

/// <summary>
/// Owns a native <c>relate_connection</c>; disconnects it on release. The connection holds a
/// reference on its database handle so the database is never closed underneath a live connection.
/// </summary>
internal sealed class ConnectionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private DatabaseHandle? _database;

    public ConnectionHandle() : base(ownsHandle: true)
    {
    }

    internal void AttachTo(DatabaseHandle database)
    {
        bool added = false;
        database.DangerousAddRef(ref added);
        if (added)
        {
            _database = database;
        }
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.relate_disconnect(handle);
        _database?.DangerousRelease();
        _database = null;
        return true;
    }
}

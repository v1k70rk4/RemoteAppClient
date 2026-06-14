namespace RemoteServer.Data;

/// <summary>Device approval/lifecycle state.</summary>
public enum DeviceStatus
{
    Pending = 0,    // enrolled but not approved yet
    Approved = 1,   // approved and usable
    Rejected = 2,   // rejected
    Revoked = 3,    // visszavont (kitiltott)
}

/// <summary>Command lifecycle in the commands queue.</summary>
public enum CommandStatus
{
    Queued = 0,     // queued, waiting for offline device
    Sent = 1,       // pushed to device
    Acked = 2,      // acknowledged by device
    Done = 3,       // completed
    Failed = 4,     // failed
}

/// <summary>User consent state for a remote session.</summary>
public enum ConsentState
{
    NotRequired = 0,
    Pending = 1,
    Granted = 2,
    Denied = 3,
}

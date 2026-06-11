namespace RemoteServer.Data;

/// <summary>Eszköz jóváhagyási/élet-állapota („licenc").</summary>
public enum DeviceStatus
{
    Pending = 0,    // beléptetett, de még nem jóváhagyott
    Approved = 1,   // jóváhagyott, használható
    Rejected = 2,   // elutasított
    Revoked = 3,    // visszavont (kitiltott)
}

/// <summary>Parancs életciklusa a commands sorban.</summary>
public enum CommandStatus
{
    Queued = 0,     // sorban, offline gépnél vár
    Sent = 1,       // push-olva a gépnek
    Acked = 2,      // a gép nyugtázta
    Done = 3,       // lefutott
    Failed = 4,     // hibára futott
}

/// <summary>Felhasználói hozzájárulás állapota egy távoli sessionhöz.</summary>
public enum ConsentState
{
    NotRequired = 0,
    Pending = 1,
    Granted = 2,
    Denied = 3,
}
